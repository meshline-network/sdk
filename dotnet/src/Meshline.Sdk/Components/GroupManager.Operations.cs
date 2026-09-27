using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Meshline.Components;

sealed partial class GroupManager
{
    static GroupOperationRecord NewOperation(GroupRef group, string method, ProtocolModel request) => new()
    {
        GroupId = group.GroupId,
        Method = method,
        RequestJson = request.ToJson()
    };

    async Task EnsureNoPendingOperationAsync(GroupRef group, string method, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        if (await database.GroupOperations.AnyAsync(value => value.GroupId == group.GroupId && value.Method == method, cancellationToken).ConfigureAwait(false))
        {
            Wake();
            throw new InvalidOperationException("An earlier group operation is awaiting confirmation. Start the component and let it recover before submitting a new operation.");
        }
    }

    async Task SaveOperationAsync(GroupOperationRecord operation, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        database.GroupOperations.Add(operation);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Wake();
    }

    async Task CompleteOperationAsync(GroupOperationRecord operation, bool confirmed, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var pending = await database.GroupOperations.SingleOrDefaultAsync(value => value.GroupId == operation.GroupId && value.Method == operation.Method && value.RequestJson == operation.RequestJson, cancellationToken).ConfigureAwait(false);
        if (pending is null) return;
        GroupChangedEventArgs? change = null;
        var record = (await database.Groups.FindAsync([operation.GroupId], cancellationToken).ConfigureAwait(false))!;
        if (confirmed)
        {
            switch (operation.Method)
            {
                case "group.close":
                    record.LocallyClosed = true;
                    change = new(GroupSnapshot(record), GroupChangeKind.Status);
                    break;
                case "group.member.leave":
                    record.Membership = GroupMembershipState.Left;
                    record.Role = null;
                    change = new(GroupSnapshot(record), GroupChangeKind.Members);
                    break;
                case "group.application.submit" when record.Membership != GroupMembershipState.Member:
                    record.Membership = GroupMembershipState.Pending;
                    change = new(GroupSnapshot(record), GroupChangeKind.Members);
                    break;
            }
        }
        if (operation.Method == "group.secret.rotation.commit" && await database.GroupRotations.FindAsync([operation.GroupId], cancellationToken).ConfigureAwait(false) is { } rotation)
            database.GroupRotations.Remove(rotation);
        database.GroupOperations.Remove(pending);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (!confirmed) return;
        Wake();
        if (change is not null) GroupChanged?.Invoke(this, change);
        var group = new GroupRef { RelayId = record.RelayId, GroupId = record.GroupId };
        if (operation.Method.StartsWith("group.application.", StringComparison.Ordinal)) ApplicationsChanged?.Invoke(this, new(group));
        if (operation.Method.StartsWith("group.member.recovery.", StringComparison.Ordinal)) KeyRecoveryChanged?.Invoke(this, new(group));
    }

    static ProtocolModel ReadOperationRequest(GroupOperationRecord operation) => operation.Method switch
    {
        "group.create" => ProtocolModel.FromJson<GroupCreateRequest>(operation.RequestJson)!,
        "group.application.approve" => ProtocolModel.FromJson<GroupApplicationApproveRequest>(operation.RequestJson)!,
        "group.member.recovery.approve" => ProtocolModel.FromJson<GroupRecoveryApproveRequest>(operation.RequestJson)!,
        "group.invite.revoke" => ProtocolModel.FromJson<GroupInviteQuery>(operation.RequestJson)!,
        "group.application.reject" or "group.member.recovery.reject" => ProtocolModel.FromJson<GroupAccountsRequest>(operation.RequestJson)!,
        _ => ProtocolModel.FromJson<TypedProtocolModel>(operation.RequestJson)!
    };

    static TypedProtocolModel? GetTimelinePayload(ProtocolModel request) => request switch
    {
        GroupCreateRequest creation => creation.Create,
        GroupApplicationApproveRequest approval => approval.Approval,
        GroupRecoveryApproveRequest recovery => recovery.Approval,
        GroupManagementOperation management => management,
        GroupMessageEnvelope message => message,
        _ => null
    };

    static HttpMethod GetOperationHttpMethod(string method) => method switch
    {
        "group.update" => HttpMethod.Patch,
        "group.member.ban" or "group.role.update" => HttpMethod.Put,
        "group.close" or "group.member.leave" or "group.member.remove" or "group.member.unban"
            or "group.invite.revoke" or "group.application.reject" or "group.member.recovery.reject" => HttpMethod.Delete,
        "group.create" or "group.message.send" or "group.invite.create" or "group.application.submit"
            or "group.application.approve" or "group.member.recovery.submit" or "group.member.recovery.approve"
            or "group.owner.transfer" or "group.secret.rotation.commit" => HttpMethod.Post,
        _ => throw new InvalidDataException("The stored group operation has an unexpected method.")
    };

    async Task<ProtocolModel?> SubmitOperationAsync(GroupOperationRecord operation, bool recovering, CancellationToken cancellationToken)
    {
        var request = ReadOperationRequest(operation);
        await using var database = new MeshlineDbContext(databaseOptions);
        if (GetTimelinePayload(request) is { } payload)
        {
            var json = payload.ToJson();
            var accepted = await database.GroupEvents.AsNoTracking().SingleOrDefaultAsync(value => value.GroupId == operation.GroupId && value.PayloadJson == json && value.Rejection == null, cancellationToken).ConfigureAwait(false);
            if (accepted is not null)
            {
                await CompleteOperationAsync(operation, true, CancellationToken.None).ConfigureAwait(false);
                return request is GroupMessageEnvelope ? new SequenceResult { Sequence = accepted.Sequence } : null;
            }
        }
        var relayId = await database.Groups.Where(value => value.GroupId == operation.GroupId).Select(value => value.RelayId).SingleAsync(cancellationToken).ConfigureAwait(false);
        if (recovering && request is Models.Protocol.GroupInvite invite)
        {
            try
            {
                var existing = await ReadInviteAsync(new(new() { RelayId = relayId, GroupId = operation.GroupId }, invite.InviteId), cancellationToken).ConfigureAwait(false);
                if (existing.Invite.Document.ToJson() != operation.RequestJson) throw new InvalidDataException("The invitation ID is occupied by a different invitation.");
                await CompleteOperationAsync(operation, true, CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch (RelayException exception) when (exception.Error.Code == "not_found") { }
        }
        var relay = await GetRelayAsync(relayId, cancellationToken).ConfigureAwait(false);
        ProtocolModel? result = null;
        try
        {
            if (operation.Method == "group.message.send")
            {
                var response = await relay.SendHttpAsync<SequenceResult>(HttpMethod.Post, operation.Method, request, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (response.Sequence <= 0) throw new InvalidDataException("The relay returned an invalid group message sequence.");
                result = response;
            }
            else if (operation.Method == "group.member.recovery.submit")
            {
                var response = await relay.SendHttpAsync<GroupRecoverySubmitResult>(HttpMethod.Post, operation.Method, request, cancellationToken: cancellationToken).ConfigureAwait(false);
                result = response;
            }
            else await relay.SendHttpAsync(GetOperationHttpMethod(operation.Method), operation.Method, request, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RelayException exception) when (!recovering && exception.Error.IsDefinitiveRejection())
        {
            await CompleteOperationAsync(operation, false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await CompleteOperationAsync(operation, true, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    async Task ManageAsync(GroupRef group, string method, Func<GroupRecord, GroupManagementOperation> create, CancellationToken cancellationToken)
    {
        await EnsureNoPendingOperationAsync(group, method, cancellationToken).ConfigureAwait(false);
        await SynchronizeCoreAsync(group, cancellationToken).ConfigureAwait(false);
        await using var database = new MeshlineDbContext(databaseOptions);
        var record = await database.Groups.AsNoTracking().SingleAsync(value => value.GroupId == group.GroupId, cancellationToken).ConfigureAwait(false);
        var request = await SignAsync(create(record), cancellationToken).ConfigureAwait(false);
        await CheckManagementAsync(group, request, cancellationToken).ConfigureAwait(false);
        var operation = NewOperation(group, method, request);
        await SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        await SubmitOperationAsync(operation, recovering: false, cancellationToken).ConfigureAwait(false);
        if (method is not ("group.close" or "group.member.leave")) await SynchronizeCoreAsync(group, cancellationToken).ConfigureAwait(false);
    }

    async Task CheckManagementAsync(GroupRef group, GroupManagementOperation operation, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        var record = await database.Groups.SingleAsync(value => value.GroupId == group.GroupId, cancellationToken).ConfigureAwait(false);
        if (record.Status != GroupStatus.Active || record.LocallyClosed || record.Membership != GroupMembershipState.Member) throw new InvalidOperationException("The group is not writable by this account.");
        var members = await database.GroupMembers.Where(value => value.GroupId == group.GroupId).ToDictionaryAsync(value => value.AccountId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var bans = await database.GroupBans.Where(value => value.GroupId == group.GroupId).ToDictionaryAsync(value => value.AccountId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var advances = operation is GroupApplicationApproval or GroupMemberLeave or GroupMemberRemoval or GroupMemberRecoveryApproval or GroupSecretRotation
            || operation is GroupMemberBan ban && ban.Accounts.Any(members.ContainsKey);
        ApplyManagement(database, record, members, bans, new() { Sequence = record.Sequence + 1, Epoch = record.Epoch + (advances ? 1 : 0), AcceptedAt = Clock.UtcNow.ToUnixTimeSeconds(), Payload = operation }, Options.AccountId);
    }

    async Task SendUnsignedAsync(GroupRef group, string method, ProtocolModel request, CancellationToken cancellationToken)
    {
        if (request.Validate(Context) is { } violation) throw new ArgumentException(violation.Message, nameof(request));
        await EnsureNoPendingOperationAsync(group, method, cancellationToken).ConfigureAwait(false);
        var operation = NewOperation(group, method, request);
        await SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        await SubmitOperationAsync(operation, recovering: false, cancellationToken).ConfigureAwait(false);
    }

    async Task<GroupMessageInfo?> SendPayloadAsync(GroupRef group, TypedProtocolModel payload, CancellationToken cancellationToken)
    {
        await EnsureNoPendingOperationAsync(group, "group.message.send", cancellationToken).ConfigureAwait(false);
        await SynchronizeCoreAsync(group, cancellationToken).ConfigureAwait(false);
        await using var database = new MeshlineDbContext(databaseOptions);
        var record = (await database.Groups.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false))!;
        if (record.Status != GroupStatus.Active || record.LocallyClosed || record.Membership != GroupMembershipState.Member) throw new InvalidOperationException("The group is not writable by this account.");
        var epoch = (await database.GroupEpochs.FindAsync([group.GroupId, record.Epoch], cancellationToken).ConfigureAwait(false))!;
        var secret = await ReadEpochAsync(epoch, cancellationToken).ConfigureAwait(false);
        GroupMessageEnvelope envelope;
        try { envelope = await EncryptMessageAsync(group.GroupId, record.Epoch, payload, secret, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(secret); }
        var operation = NewOperation(group, "group.message.send", envelope);
        await SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        var result = (SequenceResult)(await SubmitOperationAsync(operation, recovering: false, cancellationToken).ConfigureAwait(false))!;
        await SynchronizeCoreAsync(group, cancellationToken).ConfigureAwait(false);
        var stored = await database.GroupEvents.AsNoTracking().SingleOrDefaultAsync(value => value.GroupId == group.GroupId && value.Sequence == result.Sequence, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The accepted group message is absent from the synchronized timeline.");
        if (stored.Rejection is not null) throw new InvalidDataException(stored.Rejection);
        if (stored.PayloadJson != operation.RequestJson || stored.DecryptedPayloadJson != payload.ToJson()) throw new InvalidDataException("The accepted message differs from the submitted content.");
        return stored.IsMessage ? MessageSnapshot(group, stored) : null;
    }
}
