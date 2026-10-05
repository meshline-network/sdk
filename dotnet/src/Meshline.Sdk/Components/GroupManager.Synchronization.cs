using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Components;

sealed partial class GroupManager
{
    Task SynchronizeCoreAsync(GroupRef group, CancellationToken cancellationToken, bool includeKeys = true) =>
        includeKeys ? SynchronizeGroupAsync(group, cancellationToken) : SynchronizeTimelineCoreAsync(group, cancellationToken, false);

    Task<ResourceSyncStatus> SynchronizeGroupAsync(GroupRef group, CancellationToken cancellationToken, bool manual = false) =>
        _syncStatus.RunAsync(group.GroupId, async () =>
        {
            if (manual) await ProcessAccountMessagesAsync(cancellationToken).ConfigureAwait(false);
            Exception? keyError = null;
            await SynchronizeTimelineCoreAsync(group, cancellationToken, true, error => keyError ??= error).ConfigureAwait(false);
            if (keyError is not null) return new SyncBlock(ResourceSyncBlockReason.Verification, keyError);
            await using var database = new MeshlineDbContext(databaseOptions);
            var pending = await database.GroupEvents.AnyAsync(message => message.GroupId == group.GroupId
                && message.MessageId != null && message.DecryptedPayloadJson == null && message.Rejection == null
                && database.GroupEpochs.Any(epoch => epoch.GroupId == message.GroupId && epoch.Epoch == message.Epoch && epoch.MemberPublicKey != null), cancellationToken).ConfigureAwait(false);
            return pending ? new SyncBlock(ResourceSyncBlockReason.MissingKey) : null;
        }, OnSyncStatusChanged, cancellationToken, preserveOnStop: manual);

    async Task SynchronizeTimelineCoreAsync(GroupRef group, CancellationToken cancellationToken, bool includeKeys, Action<Exception>? keyFailure = null)
    {
        var relay = await GetRelayAsync(group.RelayId, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            var record = await database.Groups.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false);
            var after = record?.Sequence ?? -1;
            var page = await relay.SendHttpAsync<GroupSyncPage>(HttpMethod.Get, "group.sync", new GroupSequenceQuery { GroupId = group.GroupId, After = after }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page.Events.IsDefault || page.Certificates.IsDefault || page.HasMore && page.Events.IsEmpty) throw new InvalidDataException("The group sync page has invalid collections or pagination.");
            var certificates = ReadCertificates(page.Certificates);
            var notifications = new List<Action>();
            await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                if (record is null) database.Groups.Add(record = new() { GroupId = group.GroupId, RelayId = group.RelayId });
                if (record.RelayId != group.RelayId) throw new InvalidDataException("The group is bound to another hosting relay.");
                var members = await database.GroupMembers.Where(value => value.GroupId == group.GroupId).ToDictionaryAsync(value => value.AccountId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
                var bans = await database.GroupBans.Where(value => value.GroupId == group.GroupId).ToDictionaryAsync(value => value.AccountId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
                var pending = await database.GroupOperations.Where(value => value.GroupId == group.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
                var expected = pending.ToDictionary(value => value, value => GetTimelinePayload(ReadOperationRequest(value))?.ToJson());
                var change = (GroupChangeKind)0;
                foreach (var entry in page.Events)
                {
                    if (entry.Sequence <= record.Sequence || entry.Epoch < 0 || entry.AcceptedAt < 0 || entry.AcceptedAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                        throw new InvalidDataException("The group event order, epoch or timestamp is invalid.");
                    if (record.Status == GroupStatus.Closed && record.Sequence >= 0) throw new InvalidDataException("A closed group cannot append events.");
                    DeviceCertificate? certificate = null;
                    if (entry.SignerDeviceId is { } id && !certificates.TryGetValue(id, out certificate)) throw new InvalidDataException("The event signing certificate is missing.");
                    var stored = new GroupEventRecord { GroupId = group.GroupId, Sequence = entry.Sequence, Epoch = entry.Epoch, PayloadJson = entry.Payload.ToJson(), CertificateJson = certificate?.ToJson() };
                    if (entry.Payload is GroupMessageEnvelope envelope)
                    {
                        if (record.Sequence < 0 || entry.Epoch != record.Epoch) throw new InvalidDataException("The message does not use the current verified group epoch.");
                        try
                        {
                            if (certificate is null || !members.ContainsKey(certificate.Account) || bans.ContainsKey(certificate.Account) || envelope.GroupId != group.GroupId || envelope.Epoch != entry.Epoch)
                                throw new InvalidDataException("The group message has an invalid account, group or epoch binding.");
                            VerifySignature(envelope, certificate);
                            if (envelope.CreatedAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) throw new InvalidDataException("The group message timestamp is outside the supported range.");
                            var existing = await database.GroupEvents.AsNoTracking().SingleOrDefaultAsync(value => value.Sender == certificate.Account && value.MessageId == envelope.MessageId && value.GroupId == group.GroupId, cancellationToken).ConfigureAwait(false);
                            if (existing is not null) throw new InvalidDataException("The relay assigned another sequence to an existing logical group message.");
                            stored.MessageId = envelope.MessageId;
                            stored.Sender = certificate.Account;
                            stored.SenderDeviceId = entry.SignerDeviceId;
                            stored.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(envelope.CreatedAt);
                        }
                        catch (Exception exception) when (exception is InvalidDataException or CryptographicException or JsonException)
                        {
                            stored.Rejection = exception.Message;
                            notifications.Add(() => ReportBackgroundError(BackgroundOperation.Synchronize, group.GroupId, exception));
                        }
                    }
                    else if (entry.Payload is GroupKeyRotated)
                    {
                        if (record.Sequence < 0 || certificate is not null || entry.SignerDeviceId is not null || entry.Epoch <= record.Epoch) throw new InvalidDataException("The relay key rotation is invalid.");
                    }
                    else
                    {
                        if (certificate is null) throw new InvalidDataException("The management event has no signing device.");
                        VerifySignature(entry.Payload, certificate);
                        change |= ApplyManagement(database, record, members, bans, entry, certificate.Account);
                        record.ManagementHash = ManagementHash(entry.Payload);
                    }
                    if (record.Epoch != entry.Epoch)
                    {
                        var epoch = await database.GroupEpochs.FindAsync([group.GroupId, entry.Epoch], cancellationToken).ConfigureAwait(false);
                        if (epoch is null) database.GroupEpochs.Add(epoch = new() { GroupId = group.GroupId, Epoch = entry.Epoch });
                        epoch.Commitment = record.Commitment ?? throw new InvalidDataException("A group epoch has no verified client secret commitment.");
                        epoch.MemberPublicKey = members.GetValueOrDefault(Options.AccountId)?.PublicKey;
                    }
                    record.Epoch = entry.Epoch;
                    record.Sequence = entry.Sequence;
                    database.GroupEvents.Add(stored);
                    if (stored.Rejection is null)
                    {
                        foreach (var operation in pending.Where(value => expected[value] == stored.PayloadJson).ToArray())
                        {
                            database.GroupOperations.Remove(operation);
                            pending.Remove(operation);
                            if (operation.Method == "group.close") record.LocallyClosed = true;
                            if (operation.Method == "group.secret.rotation.commit" && await database.GroupRotations.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false) is { } rotation)
                                database.GroupRotations.Remove(rotation);
                            if (operation.Method == "group.application.approve") notifications.Add(() => ApplicationsChanged?.Invoke(this, new(group)));
                            if (operation.Method == "group.member.recovery.approve") notifications.Add(() => KeyRecoveryChanged?.Invoke(this, new(group)));
                        }
                    }
                    // Each following event observes all preceding changes from this page.
                    await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                if (change != 0)
                {
                    var snapshot = GroupSnapshot(record);
                    notifications.Add(() => GroupChanged?.Invoke(this, new(snapshot, change)));
                }
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var notification in notifications) notification();
            if (!page.HasMore) break;
        }
        if (includeKeys)
        {
            await SynchronizeKeysAsync(group, cancellationToken, keyFailure).ConfigureAwait(false);
            await DecryptPendingAsync(group, cancellationToken).ConfigureAwait(false);
        }
    }

    Dictionary<string, DeviceCertificate> ReadCertificates(IEnumerable<DeviceCertificate> certificates)
    {
        var result = new Dictionary<string, DeviceCertificate>(StringComparer.Ordinal);
        foreach (var certificate in certificates)
        {
            if (certificate.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
            if (!result.TryAdd(certificate.GetDeviceId(Context), certificate)) throw new InvalidDataException("The response contains duplicate signing devices.");
        }
        return result;
    }

    GroupChangeKind ApplyManagement(MeshlineDbContext database, GroupRecord record, Dictionary<string, GroupMemberRecord> members, Dictionary<string, GroupBanRecord> bans, GroupEvent entry, string actor)
    {
        var advances = false;
        GroupChangeKind change;
        if (entry.Payload is GroupCreate create)
        {
            if (record.Sequence != -1 || entry.Sequence != 0 || entry.Epoch != 0 || create.Owner.Account != actor || create.GroupId != record.GroupId
                || Identifiers.DeriveGroupId(actor, record.RelayId, create.Nonce.AsSpan(), Context) != record.GroupId || !Enum.IsDefined(create.InvitePolicy))
                throw new InvalidDataException("The group creation event does not establish the expected group.");
            record.Name = create.Name; record.Description = create.Description; record.Owner = actor;
            record.MemberCapacity = create.MemberCapacity; record.InvitePolicy = create.InvitePolicy; record.Status = GroupStatus.Active; record.Commitment = create.ClientSecretCommitment;
            AddMember(create.Owner, GroupRole.Owner);
            change = GroupChangeKind.Properties | GroupChangeKind.Members | GroupChangeKind.Roles | GroupChangeKind.Status;
        }
        else
        {
            if (entry.Payload is not GroupManagementOperation operation) throw new NotSupportedException("The group contains an unsupported management event: " + entry.Payload.Type);
            if (record.ManagementHash is null || operation.GroupId != record.GroupId || operation.PrevHash != record.ManagementHash || !members.TryGetValue(actor, out var author) || bans.ContainsKey(actor))
                throw new InvalidDataException("The group management chain or actor authorization is invalid.");
            var owner = author.Role == GroupRole.Owner;
            var administrator = owner || author.Role == GroupRole.Administrator;
            switch (operation)
            {
                case Models.Protocol.GroupUpdate update:
                    Require(owner);
                    if (update.InvitePolicy.IsSpecified && !Enum.IsDefined(update.InvitePolicy.Value)) throw new InvalidDataException("The invitation policy is unknown.");
                    if (update.Name.IsSpecified) record.Name = update.Name.Value;
                    if (update.Description.IsDeleted) record.Description = null;
                    else if (update.Description.IsSpecified) record.Description = update.Description.Value;
                    if (update.MemberCapacity.IsSpecified) record.MemberCapacity = update.MemberCapacity.Value;
                    if (update.InvitePolicy.IsSpecified) record.InvitePolicy = update.InvitePolicy.Value;
                    change = GroupChangeKind.Properties;
                    break;
                case GroupApplicationApproval approval:
                    Require(administrator);
                    if (members.Count + approval.Members.Length > record.MemberCapacity) throw new InvalidDataException("The admission exceeds the group capacity.");
                    foreach (var member in approval.Members)
                    {
                        if (members.ContainsKey(member.Account) || bans.ContainsKey(member.Account)) throw new InvalidDataException("The admission includes an existing or banned member.");
                        AddMember(member, GroupRole.Member);
                    }
                    advances = true; change = GroupChangeKind.Members;
                    break;
                case GroupRoleUpdate role:
                    Require(owner);
                    var target = Member(role.Account);
                    if (target.Role == GroupRole.Owner || role.Role is not (GroupRole.Member or GroupRole.Administrator)) throw new InvalidDataException("The role update cannot replace the owner.");
                    target.Role = role.Role; change = GroupChangeKind.Roles;
                    break;
                case GroupOwnerTransfer transfer:
                    Require(owner);
                    var successor = Member(transfer.NewOwnerAccount);
                    if (successor.AccountId == actor || bans.ContainsKey(successor.AccountId)) throw new InvalidDataException("The new owner must be another current member.");
                    author.Role = GroupRole.Member; successor.Role = GroupRole.Owner; record.Owner = successor.AccountId; change = GroupChangeKind.Roles;
                    break;
                case GroupMemberLeave leave:
                    Require(!owner && leave.Account == actor);
                    Remove(actor, GroupMembershipState.Left); advances = true; change = GroupChangeKind.Members;
                    break;
                case GroupMemberRemoval removal:
                    Require(administrator);
                    foreach (var account in removal.Accounts)
                    {
                        var removed = Member(account);
                        Require(account != actor && removed.Role != GroupRole.Owner && (owner || removed.Role == GroupRole.Member));
                        Remove(account, GroupMembershipState.Removed);
                    }
                    advances = true; change = GroupChangeKind.Members;
                    break;
                case GroupMemberBan ban:
                    Require(administrator);
                    foreach (var account in ban.Accounts)
                    {
                        var banned = members.GetValueOrDefault(account);
                        Require(account != actor && (banned is null || banned.Role != GroupRole.Owner && (owner || banned.Role == GroupRole.Member)));
                        if (banned is not null) { Remove(account, GroupMembershipState.Banned); advances = true; }
                        if (!bans.ContainsKey(account))
                        {
                            var item = new GroupBanRecord { GroupId = record.GroupId, AccountId = account };
                            bans.Add(account, item); database.GroupBans.Add(item);
                        }
                        if (account == Options.AccountId) record.Membership = GroupMembershipState.Banned;
                    }
                    change = GroupChangeKind.Bans | GroupChangeKind.Members;
                    break;
                case GroupMemberUnban unban:
                    Require(administrator);
                    foreach (var account in unban.Accounts)
                    {
                        if (bans.Remove(account, out var item)) database.GroupBans.Remove(item);
                        if (account == Options.AccountId && record.Membership == GroupMembershipState.Banned) record.Membership = GroupMembershipState.NotMember;
                    }
                    change = GroupChangeKind.Bans;
                    break;
                case GroupMemberRecoveryApproval recovery:
                    Require(administrator);
                    foreach (var member in recovery.Members)
                    {
                        var recovered = Member(member.Account);
                        Require(owner || recovered.Role == GroupRole.Member && member.Account != actor);
                        if (recovered.PublicKey.AsSpan().SequenceEqual(member.MemberEncryptionPublicKey.AsSpan())) throw new InvalidDataException("The recovered member public key must change.");
                        recovered.PublicKey = member.MemberEncryptionPublicKey.ToArray();
                    }
                    advances = true; change = GroupChangeKind.Members;
                    break;
                case GroupSecretRotation rotation:
                    Require(owner);
                    if (rotation.ClientSecretCommitment == record.Commitment) throw new InvalidDataException("The client secret commitment must change.");
                    record.Commitment = rotation.ClientSecretCommitment;
                    if (rotation.OwnerEncryptionPublicKey is { } key)
                    {
                        if (author.PublicKey.AsSpan().SequenceEqual(key.AsSpan())) throw new InvalidDataException("The owner public key must change when specified.");
                        author.PublicKey = key.ToArray();
                    }
                    advances = true; change = GroupChangeKind.Members;
                    break;
                case GroupClose:
                    Require(owner); record.Status = GroupStatus.Closed; change = GroupChangeKind.Status;
                    break;
                default: throw new NotSupportedException("The management operation is not supported.");
            }
            if (advances ? entry.Epoch <= record.Epoch : entry.Epoch != record.Epoch) throw new InvalidDataException("The management event has an invalid epoch transition.");
        }
        record.MemberCount = members.Count;
        record.Role = members.GetValueOrDefault(Options.AccountId)?.Role;
        if (record.Role is not null) record.Membership = GroupMembershipState.Member;
        else if (record.Membership == GroupMembershipState.Unknown) record.Membership = GroupMembershipState.NotMember;
        return change;

        GroupMemberRecord Member(string account) => members.GetValueOrDefault(account) ?? throw new InvalidDataException("The management target is not a current member.");
        static void Require(bool authorized) { if (!authorized) throw new InvalidDataException("The management actor is not authorized for this operation."); }
        void AddMember(GroupMemberKey key, GroupRole role)
        {
            var member = new GroupMemberRecord { GroupId = record.GroupId, AccountId = key.Account, Role = role, PublicKey = key.MemberEncryptionPublicKey.ToArray(), JoinedAtSequence = entry.Sequence };
            members.Add(key.Account, member); database.GroupMembers.Add(member);
        }
        void Remove(string account, GroupMembershipState membership)
        {
            database.GroupMembers.Remove(members[account]); members.Remove(account);
            if (account == Options.AccountId) record.Membership = membership;
        }
    }
}
