using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc7748;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Meshline.Components;

sealed partial class GroupManager
{
    async Task<MessageEffect> PreparePrivateStatesAsync(AccountGroupPrivateStateSync payload, CancellationToken cancellationToken)
    {
        var states = new List<(GroupMemberPrivateState State, string PublicKey, byte[] ProtectedKey)>();
        foreach (var state in payload.States)
        {
            var publicKey = new byte[32];
            X25519.GeneratePublicKey(state.MemberEncryptionPrivateKey.AsSpan(), publicKey);
            var encoded = Base64Url.EncodeToString(publicKey);
            var protectedKey = await secretProtector.ProtectAsync(state.MemberEncryptionPrivateKey.AsMemory(), SecretPurpose(state.GroupId, "member", encoded), cancellationToken).ConfigureAwait(false);
            states.Add((state, encoded, protectedKey));
        }
        return async (database, token) =>
        {
            foreach (var (state, publicKey, protectedKey) in states)
            {
                var group = await database.Groups.FindAsync([state.GroupId], token).ConfigureAwait(false);
                if (group is null) database.Groups.Add(new() { GroupId = state.GroupId, RelayId = state.RelayId });
                else if (group.RelayId != state.RelayId) throw new InvalidDataException("The synchronized private state identifies another hosting relay.");
                if (await database.GroupMemberKeys.FindAsync([state.GroupId, publicKey], token).ConfigureAwait(false) is null)
                    database.GroupMemberKeys.Add(new() { GroupId = state.GroupId, PublicKey = publicKey, ProtectedPrivateKey = protectedKey });
            }
        };
    }

    async Task<MessageEffect> PrepareHistorySecretsAsync(AccountGroupHistorySecretSync payload, CancellationToken cancellationToken)
    {
        var secrets = new List<(GroupHistorySecret Secret, byte[] Protected)>();
        foreach (var item in payload.Secrets)
            secrets.Add((item, await ProtectEpochAsync(item.GroupId, item.Epoch, item.ApplicationSecret.ToArray(), cancellationToken).ConfigureAwait(false)));
        return async (database, token) =>
        {
            foreach (var (secret, protectedValue) in secrets)
            {
                var record = await database.GroupEpochs.FindAsync([secret.GroupId, secret.Epoch], token).ConfigureAwait(false);
                if (record is null) database.GroupEpochs.Add(record = new() { GroupId = secret.GroupId, Epoch = secret.Epoch });
                if (record.ProtectedApplicationSecret is not null)
                {
                    var existing = await ReadEpochAsync(record, token).ConfigureAwait(false);
                    try { if (!CryptographicOperations.FixedTimeEquals(existing, secret.ApplicationSecret.AsSpan())) throw new InvalidDataException("The synchronized epoch secret conflicts with an existing secret."); }
                    finally { CryptographicOperations.ZeroMemory(existing); }
                }
                else record.ProtectedApplicationSecret = protectedValue;
            }
        };
    }

    async Task ReplyToPrivateStateRequestAsync(string senderDeviceId, AccountGroupPrivateStateRequest request, CancellationToken cancellationToken)
    {
        if (senderDeviceId == Certificate.GetDeviceId(Context)) return;
        await using var database = new MeshlineDbContext(databaseOptions);
        var requested = request.GroupId;
        string after = "";
        while (true)
        {
            var groups = await database.Groups.AsNoTracking().Where(value => (requested == null || value.GroupId == requested) && value.Membership == GroupMembershipState.Member
                && string.Compare(value.GroupId, after) > 0).OrderBy(value => value.GroupId).Take(32).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (groups.Count == 0) break;
            foreach (var group in groups)
            {
                await SendPrivateStateAsync(database, group, [senderDeviceId], cancellationToken).ConfigureAwait(false);
                long epoch = -1;
                while (true)
                {
                    var records = await database.GroupEpochs.AsNoTracking().Where(value => value.GroupId == group.GroupId && value.Epoch > epoch && value.Commitment != null && value.ProtectedApplicationSecret != null)
                        .OrderBy(value => value.Epoch).Take(64).ToListAsync(cancellationToken).ConfigureAwait(false);
                    if (records.Count == 0) break;
                    var batch = new List<GroupHistorySecret>();
                    foreach (var record in records)
                    {
                        var secret = await ReadEpochAsync(record, cancellationToken).ConfigureAwait(false);
                        try { batch.Add(new() { GroupId = group.GroupId, Epoch = record.Epoch, ApplicationSecret = [.. secret] }); }
                        finally { CryptographicOperations.ZeroMemory(secret); }
                    }
                    await messageManager.SendPayloadAsync(Options.AccountId, new AccountGroupHistorySecretSync { Secrets = [.. batch] }, cancellationToken, recipientDeviceIds: [senderDeviceId]).ConfigureAwait(false);
                    epoch = records[^1].Epoch;
                }
            }
            after = groups[^1].GroupId;
        }
    }

    async Task ShareCurrentKeyAsync(GroupRef group, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        var state = await database.Groups.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false);
        if (state?.Membership != GroupMembershipState.Member) return;
        var member = await database.GroupMembers.FindAsync([group.GroupId, Options.AccountId], cancellationToken).ConfigureAwait(false);
        if (member is null) return;
        var key = await database.GroupMemberKeys.FindAsync([group.GroupId, Base64Url.EncodeToString(member.PublicKey)], cancellationToken).ConfigureAwait(false);
        if (key is null || key.Shared) return;
        if (await SendPrivateStateAsync(database, state, null, cancellationToken).ConfigureAwait(false))
        {
            key.Shared = true;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    async Task<bool> SendPrivateStateAsync(MeshlineDbContext database, GroupRecord group, IReadOnlyList<string>? devices, CancellationToken cancellationToken)
    {
        if (group.Membership != GroupMembershipState.Member) return false;
        var epoch = await database.GroupEpochs.FindAsync([group.GroupId, group.Epoch], cancellationToken).ConfigureAwait(false);
        if (epoch?.MemberPublicKey is null || epoch.ProtectedClientSecret is null) return false;
        var privateKey = await ReadMemberKeyAsync(database, group.GroupId, epoch.MemberPublicKey, cancellationToken).ConfigureAwait(false);
        if (privateKey is null) return false;
        try
        {
            await messageManager.SendPayloadAsync(Options.AccountId, new AccountGroupPrivateStateSync { States = [new() { GroupId = group.GroupId, RelayId = group.RelayId, MemberEncryptionPrivateKey = [.. privateKey] }] }, cancellationToken, recipientDeviceIds: devices).ConfigureAwait(false);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }
}
