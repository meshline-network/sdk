using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc7748;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Components;

sealed partial class GroupManager
{
    async Task RecoverLocalMessagesAsync(CancellationToken cancellationToken)
    {
        string after = "";
        while (true)
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            var groups = await database.Groups.AsNoTracking().Where(group => string.Compare(group.GroupId, after) > 0
                && database.GroupEpochs.Any(epoch => epoch.GroupId == group.GroupId && epoch.Commitment != null
                    && (epoch.KeyEntryJson != null && epoch.ProtectedClientSecret == null
                        || epoch.ProtectedApplicationSecret != null && database.GroupEvents.Any(message => message.GroupId == group.GroupId && message.Epoch == epoch.Epoch
                            && message.MessageId != null && message.DecryptedPayloadJson == null && message.Rejection == null))))
                .OrderBy(group => group.GroupId).Take(32).Select(group => new GroupRef { GroupId = group.GroupId, RelayId = group.RelayId }).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (groups.Count == 0) return;
            foreach (var group in groups)
            {
                try
                {
                    await RunAsync(async token =>
                    {
                        await DerivePendingEpochsAsync(group.GroupId, token).ConfigureAwait(false);
                        await DecryptPendingAsync(group, token).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, group.GroupId, exception); }
            }
            after = groups[^1].GroupId;
        }
    }

    async Task SynchronizeKeysAsync(GroupRef group, CancellationToken cancellationToken, Action<Exception>? keyFailure = null)
    {
        var relay = await GetRelayAsync(group.RelayId, cancellationToken).ConfigureAwait(false);
        long after;
        await using (var database = new MeshlineDbContext(databaseOptions))
            after = await database.GroupEpochs.Where(value => value.GroupId == group.GroupId && value.KeyEntryJson != null).Select(value => (long?)value.Epoch).MaxAsync(cancellationToken).ConfigureAwait(false) ?? -1;
        while (true)
        {
            var page = await relay.SendHttpAsync<GroupKeyPage>(HttpMethod.Get, "group.key.sync", new GroupSequenceQuery { GroupId = group.GroupId, After = after }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page.Validate() is { } violation) throw new InvalidDataException(violation.Message);
            await using var database = new MeshlineDbContext(databaseOptions);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            GroupSecretBox? previousBox = null;
            GroupEpochRecord? previous = null;
            foreach (var entry in page.Keys)
            {
                if (entry.Epoch <= after) throw new InvalidDataException("The key page is not ordered after the requested epoch.");
                var epoch = await database.GroupEpochs.FindAsync([group.GroupId, entry.Epoch], cancellationToken).ConfigureAwait(false);
                if (epoch?.Commitment is null) throw new InvalidOperationException("The group management history has not established the requested key epoch.");
                if (epoch.MemberPublicKey is null) throw new InvalidDataException("The relay returned keys for an epoch where this account was not a member.");
                var changed = previous is null || previous.Commitment != epoch.Commitment || !previous.MemberPublicKey!.AsSpan().SequenceEqual(epoch.MemberPublicKey);
                if (changed && entry.ClientSecretBox is null) throw new InvalidDataException("The key page omitted a required client secret box.");
                var box = entry.ClientSecretBox ?? previousBox!;
                epoch.KeyEntryJson = (entry with { ClientSecretBox = box }).ToJson();
                previous = epoch; previousBox = box; after = entry.Epoch;
            }
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (!page.HasMore) break;
        }
        await DerivePendingEpochsAsync(group.GroupId, cancellationToken, keyFailure).ConfigureAwait(false);
    }

    async Task DerivePendingEpochsAsync(string groupId, CancellationToken cancellationToken, Action<Exception>? keyFailure = null)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        var epochs = await database.GroupEpochs.Where(value => value.GroupId == groupId && value.KeyEntryJson != null && value.ProtectedClientSecret == null).OrderBy(value => value.Epoch).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var epoch in epochs)
        {
            if (epoch.MemberPublicKey is null || epoch.Commitment is null) continue;
            var privateKey = await ReadMemberKeyAsync(database, groupId, epoch.MemberPublicKey, cancellationToken).ConfigureAwait(false);
            if (privateKey is null) continue;
            byte[]? clientSecret = null;
            byte[]? relaySecret = null;
            byte[]? applicationSecret = null;
            var shared = new byte[32];
            var material = new byte[64];
            try
            {
                var entry = ProtocolModel.FromJson<GroupKeyEntry>(epoch.KeyEntryJson!)!;
                var clientBox = entry.ClientSecretBox!;
                if (!X25519.CalculateAgreement(privateKey.AsSpan(), clientBox.Enc.AsSpan(), shared)) throw new CryptographicException("The client secret box has an all-zero agreement.");
                var clientAad = new ClientBoxInput { GroupId = groupId, Account = Options.AccountId, MemberEncryptionPublicKey = [.. epoch.MemberPublicKey], ClientSecretCommitment = epoch.Commitment }.GetSigningInput(Context);
                clientSecret = OpenSecret(clientBox, shared, clientAad);
                if (Commitment(groupId, clientSecret) != epoch.Commitment) throw new CryptographicException("The client secret does not match its verified group commitment.");
                CryptographicOperations.ZeroMemory(shared);
                shared = await deviceManager.DeriveSharedSecretAsync(entry.RelaySecretBox.Enc.AsMemory(), cancellationToken).ConfigureAwait(false);
                var relayAad = new RelayBoxInput { GroupId = groupId, Account = Options.AccountId, DeviceId = Certificate.GetDeviceId(Context), Epoch = epoch.Epoch }.GetSigningInput(Context);
                relaySecret = OpenSecret(entry.RelaySecretBox, shared, relayAad);
                clientSecret.CopyTo(material, 0); relaySecret.CopyTo(material, 32);
                applicationSecret = Derive(material, "Meshline/group-epoch-salt/v1", new EpochInput { GroupId = groupId, Epoch = epoch.Epoch, ClientSecretCommitment = epoch.Commitment }.GetSigningInput(Context));
                if (epoch.ProtectedApplicationSecret is { } existing)
                {
                    var previous = await secretProtector.UnprotectAsync(existing, SecretPurpose(groupId, "epoch", epoch.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);
                    try { if (!CryptographicOperations.FixedTimeEquals(previous, applicationSecret)) throw new CryptographicException("The derived epoch secret conflicts with a previously synchronized secret."); }
                    finally { CryptographicOperations.ZeroMemory(previous); }
                }
                var protectedClientSecret = await secretProtector.ProtectAsync(clientSecret, SecretPurpose(groupId, "client", epoch.Commitment), cancellationToken).ConfigureAwait(false);
                var protectedApplicationSecret = epoch.ProtectedApplicationSecret ?? await ProtectEpochAsync(groupId, epoch.Epoch, applicationSecret, cancellationToken).ConfigureAwait(false);
                epoch.ProtectedClientSecret = protectedClientSecret;
                epoch.ProtectedApplicationSecret = protectedApplicationSecret;
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or CryptographicException or ArgumentException { ParamName: "peerPublicKey" })
            {
                keyFailure?.Invoke(exception);
                ReportBackgroundError(BackgroundOperation.Synchronize, groupId, exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey); CryptographicOperations.ZeroMemory(shared); CryptographicOperations.ZeroMemory(material);
                if (clientSecret is not null) CryptographicOperations.ZeroMemory(clientSecret);
                if (relaySecret is not null) CryptographicOperations.ZeroMemory(relaySecret);
                if (applicationSecret is not null) CryptographicOperations.ZeroMemory(applicationSecret);
            }
        }
    }

    Task<byte[]> ProtectEpochAsync(string groupId, long epoch, byte[] secret, CancellationToken cancellationToken) =>
        secretProtector.ProtectAsync(secret, SecretPurpose(groupId, "epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);

    Task<byte[]> ReadEpochAsync(GroupEpochRecord record, CancellationToken cancellationToken) =>
        secretProtector.UnprotectAsync(record.ProtectedApplicationSecret ?? throw new InvalidOperationException("The group application secret is unavailable."), SecretPurpose(record.GroupId, "epoch", record.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);

    async Task DecryptPendingAsync(GroupRef group, CancellationToken cancellationToken)
    {
        long after = -1;
        while (true)
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var records = await database.GroupEvents.Where(value => value.GroupId == group.GroupId && value.Sequence > after && value.MessageId != null && value.DecryptedPayloadJson == null && value.Rejection == null)
                .OrderBy(value => value.Sequence).Take(128).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (records.Count == 0) return;
            var messages = new List<GroupMessageInfo>();
            var notifications = new List<Action>();
            foreach (var record in records)
            {
                after = record.Sequence;
                var epoch = await database.GroupEpochs.FindAsync([group.GroupId, record.Epoch], cancellationToken).ConfigureAwait(false);
                if (epoch?.Commitment is null || epoch.ProtectedApplicationSecret is null) continue;
                var secret = await ReadEpochAsync(epoch, cancellationToken).ConfigureAwait(false);
                try
                {
                    var envelope = ProtocolModel.FromJson<GroupMessageEnvelope>(record.PayloadJson)!;
                    var payload = DecryptMessage(envelope, record.Sender!, record.SenderDeviceId!, secret);
                    if (payload is GroupMessage or GroupMemberNicknameUpdate && payload.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
                    if (payload is GroupMessage message)
                    {
                        if (message.ReplyToSeq >= record.Sequence) throw new InvalidDataException("The reply sequence must precede the containing group message.");
                    }
                    else if (payload is GroupMemberNicknameUpdate nickname)
                    {
                        var member = await database.GroupMembers.FindAsync([group.GroupId, record.Sender!], cancellationToken).ConfigureAwait(false);
                        if (member is not null && record.Sequence >= member.JoinedAtSequence && record.Sequence > member.NicknameSequence)
                        {
                            member.Nickname = nickname.Nickname; member.NicknameSequence = record.Sequence;
                            var snapshot = GroupSnapshot((await database.Groups.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false))!);
                            notifications.Add(() => GroupChanged?.Invoke(this, new(snapshot, GroupChangeKind.Nickname)));
                        }
                    }
                    record.DecryptedPayloadJson = payload.ToJson();
                    if (payload is GroupMessage)
                    {
                        record.IsMessage = true;
                        messages.Add(MessageSnapshot(group, record));
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or CryptographicException or JsonException or DecoderFallbackException)
                {
                    record.Rejection = exception.Message;
                    notifications.Add(() => ReportBackgroundError(BackgroundOperation.Synchronize, group.GroupId, exception));
                }
                finally { CryptographicOperations.ZeroMemory(secret); }
            }
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (messages.Count > 0) TimelineChanged?.Invoke(this, new(messages));
            foreach (var notification in notifications) notification();
        }
    }
}
