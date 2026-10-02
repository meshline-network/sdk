using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Security.Cryptography;
using ClientInvite = Meshline.Models.Client.GroupInvite;
using SignedInvite = Meshline.Models.Protocol.GroupInvite;

namespace Meshline.Components;

sealed partial class GroupManager
{
    Task<ClientInvite> CreateInviteCoreAsync(GroupRef group, string? invitee, DateTimeOffset expiry, long? maxUses, CancellationToken cancellationToken) => RunAsync(async token =>
    {
        await EnsureNoPendingOperationAsync(group, "group.invite.create", token).ConfigureAwait(false);
        await SynchronizeCoreAsync(group, token, false).ConfigureAwait(false);
        var snapshot = await ReadGroupAsync(group, token).ConfigureAwait(false);
        if (snapshot.Membership != GroupMembershipState.Member || snapshot.Group.Status != GroupStatus.Active || snapshot.Role == GroupRole.Member
            && (snapshot.Group.InvitePolicy == GroupInvitePolicy.Administrators || invitee is null && snapshot.Group.InvitePolicy != GroupInvitePolicy.MembersShareable))
            throw new UnauthorizedAccessException("The account cannot create this group invitation.");
        var relay = await GetRelayAsync(group.RelayId, token).ConfigureAwait(false);
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        var limits = (await relay.GetInfoAsync(token).ConfigureAwait(false)).Limits;
        if (expiry.ToUnixTimeSeconds() - now > limits.MaxGroupInviteTtl) throw new ArgumentOutOfRangeException(nameof(expiry), "The invitation lifetime exceeds the hosting relay limit.");
        var invite = await SignAsync(new SignedInvite
        {
            InviteId = Identifiers.CreateInviteId(),
            GroupId = group.GroupId,
            Inviter = Options.AccountId,
            Invitee = invitee,
            MaxUses = maxUses,
            CreatedAt = now,
            ExpiresAt = expiry.ToUnixTimeSeconds(),
            DeviceSignature = []
        }, token).ConfigureAwait(false);
        var operation = NewOperation(group, "group.invite.create", invite);
        await SaveOperationAsync(operation, token).ConfigureAwait(false);
        await SubmitOperationAsync(operation, recovering: false, token).ConfigureAwait(false);
        return new ClientInvite(group.RelayId, invite);
    }, cancellationToken);

    async Task<GroupInviteInfo> ReadInviteAsync(GroupInviteRef reference, CancellationToken cancellationToken)
    {
        var relay = await GetRelayAsync(reference.Group.RelayId, cancellationToken).ConfigureAwait(false);
        var result = await relay.SendHttpAsync<GroupInviteResolveResult>(HttpMethod.Get, "group.invite.resolve", new GroupInviteQuery { GroupId = reference.Group.GroupId, InviteId = reference.InviteId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Invite.InviteId != reference.InviteId) throw new InvalidDataException("The relay returned another invitation.");
        return CheckInvite(reference.Group, result);
    }

    GroupInviteInfo CheckInvite(GroupRef group, GroupInviteResolveResult result)
    {
        var invite = result.Invite;
        var certificate = result.SignerCertificate;
        if (result.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
        if (invite.GroupId != group.GroupId || invite.Inviter != certificate.Account)
            throw new InvalidDataException("The group invitation has invalid identity or usage fields.");
        VerifySignature(invite, certificate);
        return new() { Invite = new ClientInvite(group.RelayId, invite), Uses = result.Uses };
    }

    async Task<Page<GroupInviteInfo>> ReadInvitesAsync(GroupRef group, PageRequest? page, CancellationToken cancellationToken)
    {
        var relay = await GetRelayAsync(group.RelayId, cancellationToken).ConfigureAwait(false);
        var result = await relay.SendHttpAsync<GroupInvitePage>(HttpMethod.Get, "group.invite.list", ListQuery(group, page), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Invites.IsDefault || result.Certificates.IsDefault || result.Next is not null && (result.Next == page?.Cursor || result.Invites.IsEmpty)) throw new InvalidDataException("The invitation page is invalid.");
        var certificates = ReadCertificates(result.Certificates);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<GroupInviteInfo>();
        foreach (var entry in result.Invites)
        {
            if (!ids.Add(entry.Invite.InviteId) || !certificates.TryGetValue(entry.SignerDeviceId, out var certificate)) throw new InvalidDataException("The invitation page repeats an invitation or omits its signing device.");
            values.Add(CheckInvite(group, new GroupInviteResolveResult { Invite = entry.Invite, SignerCertificate = certificate, Uses = entry.Uses }));
        }
        return new(values, result.Next);
    }

    async Task ApplyAsync(ClientInvite invite, CancellationToken cancellationToken)
    {
        var group = invite.Group;
        if (invite.Document.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
        if (invite.Document.Invitee is { } target && target != Options.AccountId) throw new InvalidDataException("The invitation does not authorize this application.");
        await EnsureNoPendingOperationAsync(group, "group.application.submit", cancellationToken).ConfigureAwait(false);
        GroupOperationRecord operation;
        // The host checks its accepted invitation by ID; invitation-record reads require membership.
        var relay = await GetRelayAsync(group.RelayId, cancellationToken).ConfigureAwait(false);
        var preview = await relay.SendHttpAsync<GroupState>(HttpMethod.Get, "group.resolve", new GroupResolveQuery { GroupId = group.GroupId, InviteId = invite.Document.InviteId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        CheckPreview(preview, group);
        await using var database = new MeshlineDbContext(databaseOptions);
        var privateKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var publicKey = await SaveMemberKeyAsync(database, group.GroupId, privateKey, cancellationToken).ConfigureAwait(false);
            var application = await SignAsync(new GroupApplication { GroupId = group.GroupId, Account = Options.AccountId, InviteId = invite.Document.InviteId, MemberEncryptionPublicKey = [.. publicKey], DeviceSignature = [] }, cancellationToken).ConfigureAwait(false);
            operation = NewOperation(group, "group.application.submit", application);
            database.GroupOperations.Add(operation);
            var record = await database.Groups.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false);
            if (record is null) database.Groups.Add(record = new() { GroupId = group.GroupId, RelayId = group.RelayId });
            if (record.RelayId != group.RelayId) throw new InvalidDataException("The group is bound to another relay.");
            ApplyPreview(record, preview);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        await SubmitOperationAsync(operation, recovering: false, cancellationToken).ConfigureAwait(false);
        Wake();
    }

    async Task<Page<GroupApplicationInfo>> ReadApplicationsAsync(GroupRef group, PageRequest? page, CancellationToken cancellationToken)
    {
        var relay = await GetRelayAsync(group.RelayId, cancellationToken).ConfigureAwait(false);
        var result = await relay.SendHttpAsync<GroupApplicationPage>(HttpMethod.Get, "group.application.list", ListQuery(group, page), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Applications.IsDefault || result.Next is not null && (result.Next == page?.Cursor || result.Applications.IsEmpty)) throw new InvalidDataException("The application page is invalid.");
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<GroupApplicationInfo>();
        foreach (var entry in result.Applications)
        {
            if (!accounts.Add(entry.Application.Account) || entry.Application.GroupId != group.GroupId || entry.Application.Account != entry.SignerCertificate.Account)
                throw new InvalidDataException("The group application has invalid identity or acceptance fields.");
            if (entry.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
            VerifySignature(entry.Application, entry.SignerCertificate);
            values.Add(new() { Ref = group, Application = entry.Application, SignerCertificate = entry.SignerCertificate, AcceptedAt = DateTimeOffset.FromUnixTimeSeconds(entry.AcceptedAt) });
        }
        return new(values, result.Next);
    }

    async Task<Page<GroupKeyRecoveryInfo>> ReadRecoveriesAsync(GroupRef group, PageRequest? page, CancellationToken cancellationToken)
    {
        var relay = await GetRelayAsync(group.RelayId, cancellationToken).ConfigureAwait(false);
        var result = await relay.SendHttpAsync<GroupRecoveryPage>(HttpMethod.Get, "group.member.recovery.list", ListQuery(group, page), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Requests.IsDefault || result.Next is not null && (result.Next == page?.Cursor || result.Requests.IsEmpty)) throw new InvalidDataException("The recovery page is invalid.");
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<GroupKeyRecoveryInfo>();
        foreach (var entry in result.Requests)
        {
            if (!accounts.Add(entry.Request.Account) || entry.Request.GroupId != group.GroupId || entry.Request.Account != entry.SignerCertificate.Account)
                throw new InvalidDataException("The recovery request has invalid identity or acceptance fields.");
            if (entry.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
            VerifySignature(entry.Request, entry.SignerCertificate);
            values.Add(new() { Group = group, Request = entry.Request, SignerCertificate = entry.SignerCertificate, AcceptedAt = DateTimeOffset.FromUnixTimeSeconds(entry.AcceptedAt), ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(entry.ExpiresAt) });
        }
        return new(values, result.Next);
    }

    async Task<GroupKeyRecoveryInfo> RequestRecoveryAsync(GroupRef group, CancellationToken cancellationToken)
    {
        await EnsureNoPendingOperationAsync(group, "group.member.recovery.submit", cancellationToken).ConfigureAwait(false);
        GroupOperationRecord operation;
        await SynchronizeCoreAsync(group, cancellationToken, false).ConfigureAwait(false);
        await using var database = new MeshlineDbContext(databaseOptions);
        var privateKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var publicKey = await SaveMemberKeyAsync(database, group.GroupId, privateKey, cancellationToken).ConfigureAwait(false);
            var request = await SignAsync(new GroupMemberRecoveryRequest { GroupId = group.GroupId, Account = Options.AccountId, MemberEncryptionPublicKey = [.. publicKey], DeviceSignature = [] }, cancellationToken).ConfigureAwait(false);
            operation = NewOperation(group, "group.member.recovery.submit", request);
            database.GroupOperations.Add(operation);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        var result = (GroupRecoverySubmitResult)(await SubmitOperationAsync(operation, recovering: false, cancellationToken).ConfigureAwait(false))!;
        var info = new GroupKeyRecoveryInfo
        {
            Group = group,
            Request = ProtocolModel.FromJson<GroupMemberRecoveryRequest>(operation.RequestJson)!,
            SignerCertificate = Certificate,
            AcceptedAt = DateTimeOffset.FromUnixTimeSeconds(result.AcceptedAt),
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(result.ExpiresAt)
        };
        return info;
    }

    async Task ApproveAsync(GroupRef group, IReadOnlyList<string> accounts, bool recovery, CancellationToken cancellationToken)
    {
        var method = recovery ? "group.member.recovery.approve" : "group.application.approve";
        await EnsureNoPendingOperationAsync(group, method, cancellationToken).ConfigureAwait(false);
        GroupOperationRecord operation;
        await SynchronizeCoreAsync(group, cancellationToken).ConfigureAwait(false);
        if (accounts.Count == 0 || accounts.Distinct(StringComparer.Ordinal).Count() != accounts.Count) throw new ArgumentException("The account list must be nonempty and unique.", nameof(accounts));
        var targets = accounts.ToHashSet(StringComparer.Ordinal);
        var members = new List<GroupMemberKey>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            if (recovery)
            {
                var page = await ReadRecoveriesAsync(group, new() { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items.Where(value => targets.Contains(value.Request.Account)))
                {
                    if (item.ExpiresAt <= Clock.UtcNow) throw new InvalidOperationException("The selected recovery request has expired.");
                    members.Add(new() { Account = item.Request.Account, MemberEncryptionPublicKey = item.Request.MemberEncryptionPublicKey });
                }
                cursor = page.NextCursor;
            }
            else
            {
                var page = await ReadApplicationsAsync(group, new() { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items.Where(value => targets.Contains(value.Application.Account)))
                {
                    var invite = (await ReadInviteAsync(new(group, item.Application.InviteId), cancellationToken).ConfigureAwait(false)).Invite.Document;
                    if (invite.Invitee is { } account && account != item.Application.Account) throw new InvalidDataException("The application invitation is intended for another account.");
                    members.Add(new() { Account = item.Application.Account, MemberEncryptionPublicKey = item.Application.MemberEncryptionPublicKey });
                }
                cursor = page.NextCursor;
            }
            if (cursor is not null && !cursors.Add(cursor)) throw new InvalidDataException("The group list repeated a continuation cursor.");
        } while (cursor is not null && members.Count < accounts.Count);
        if (members.Count != accounts.Count || members.Select(value => value.Account).Distinct(StringComparer.Ordinal).Count() != accounts.Count) throw new InvalidOperationException("At least one selected request is missing or changed during pagination.");
        await using var database = new MeshlineDbContext(databaseOptions);
        var state = (await database.Groups.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false))!;
        GroupManagementOperation approval = recovery
            ? new GroupMemberRecoveryApproval { GroupId = group.GroupId, PrevHash = state.ManagementHash!, Members = [.. members], DeviceSignature = [] }
            : new GroupApplicationApproval { GroupId = group.GroupId, PrevHash = state.ManagementHash!, Members = [.. members], DeviceSignature = [] };
        approval = await SignAsync(approval, cancellationToken).ConfigureAwait(false);
        await CheckManagementAsync(group, approval, cancellationToken).ConfigureAwait(false);
        var epoch = (await database.GroupEpochs.FindAsync([group.GroupId, state.Epoch], cancellationToken).ConfigureAwait(false))!;
        var secret = await secretProtector.UnprotectAsync(epoch.ProtectedClientSecret ?? throw new InvalidOperationException("The current group client secret is unavailable."), SecretPurpose(group.GroupId, "client", state.Commitment!), cancellationToken).ConfigureAwait(false);
        try
        {
            var boxes = members.ToImmutableDictionary(value => value.Account, value => SealClientSecret(group.GroupId, value.Account, value.MemberEncryptionPublicKey.AsSpan(), state.Commitment!, secret), StringComparer.Ordinal);
            ProtocolModel request = recovery
                ? new GroupRecoveryApproveRequest { Approval = (GroupMemberRecoveryApproval)approval, ClientSecretCommitment = state.Commitment!, ClientSecretBoxes = boxes }
                : new GroupApplicationApproveRequest { Approval = (GroupApplicationApproval)approval, ClientSecretCommitment = state.Commitment!, ClientSecretBoxes = boxes };
            operation = NewOperation(group, method, request);
            await SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        await SubmitOperationAsync(operation, recovering: false, cancellationToken).ConfigureAwait(false);
        await SynchronizeCoreAsync(group, cancellationToken).ConfigureAwait(false);
        await ShareCurrentKeyAsync(group, cancellationToken).ConfigureAwait(false);
    }
}
