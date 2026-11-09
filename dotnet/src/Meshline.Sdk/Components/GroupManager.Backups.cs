using Meshline.Backups;
using Meshline.Identity;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc7748;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using static Meshline.Backups.BackupData;

namespace Meshline.Components;

sealed partial class GroupManager
{
    /// <summary>Exports this component's portable logical records from a consistent local snapshot.</summary>
    /// <remarks>Records are plaintext. Use <see cref="MeshlineClient"/> for encrypted backup files. This operation does not access the network. A local device is required only when unprotecting group secrets.</remarks>
    /// <param name="history">Optional group history selection; defaults to <see cref="BackupSelection.None"/>. Base data is always exported.</param>
    /// <param name="cancellationToken">Cancels enumeration; component disposal also cancels it.</param>
    /// <returns>Complete JSON records with <c>type</c> and <c>data</c> fields, independent of internal documents and valid after enumeration ends.</returns>
    public async IAsyncEnumerable<JsonElement> ExportBackupAsync(BackupSelection? history = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        history ??= BackupSelection.None;
        if (!ValidSelection(history, group: true, cancellationToken))
            throw new ArgumentException("Invalid history selection.", nameof(history));
        await using var database = new MeshlineDbContext(databaseOptions);
        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await foreach (var group in database.Groups.AsNoTracking().OrderBy(value => value.GroupId).AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return Record("group", new GroupData(group.GroupId, group.RelayId, group.LocallyClosed));
        await foreach (var entry in database.GroupEvents.AsNoTracking().OrderBy(value => value.GroupId).ThenBy(value => value.Sequence).AsAsyncEnumerable().WithCancellation(cancellationToken))
            if (history.Includes(entry.GroupId) || ProtocolModel.FromJson<TypedProtocolModel>(entry.PayloadJson) is not GroupMessageEnvelope)
                yield return Record("group_event", Project(entry, EventFields));
        await foreach (var member in database.GroupMembers.AsNoTracking().Where(value => value.NicknameSequence >= 0)
            .OrderBy(value => value.GroupId).ThenBy(value => value.AccountId).AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return Record("group_nickname", new NicknameData(member.GroupId, member.AccountId, member.JoinedAtSequence, member.NicknameSequence, member.Nickname));
        await foreach (var epoch in database.GroupEpochs.AsNoTracking().OrderBy(value => value.GroupId).ThenBy(value => value.Epoch).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            byte[]? client = null;
            byte[]? application = null;
            JsonElement record;
            try
            {
                if (epoch.ProtectedClientSecret is { } protectedClient)
                    client = await ReadSecretAsync("client", epoch.GroupId, epoch.Commitment ?? throw new InvalidDataException("A group client secret lacks its commitment."), protectedClient).ConfigureAwait(false);
                if (epoch.ProtectedApplicationSecret is { } protectedApplication)
                    application = await ReadSecretAsync("epoch", epoch.GroupId, epoch.Epoch.ToString(CultureInfo.InvariantCulture), protectedApplication).ConfigureAwait(false);
                record = Record("group_epoch", new EpochData(epoch.GroupId, epoch.Epoch, epoch.Commitment, epoch.MemberPublicKey, client, application));
            }
            finally
            {
                if (client is not null)
                    CryptographicOperations.ZeroMemory(client);
                if (application is not null)
                    CryptographicOperations.ZeroMemory(application);
            }
            yield return record;
        }
        await foreach (var member in database.GroupMemberKeys.AsNoTracking().OrderBy(value => value.GroupId).ThenBy(value => value.PublicKey).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var bytes = await ReadSecretAsync("member", member.GroupId, member.PublicKey, member.ProtectedPrivateKey).ConfigureAwait(false);
            JsonElement record;
            try
            {
                record = Record("group_secret", new SecretData(member.GroupId, "member", member.PublicKey, bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            yield return record;
        }
        async Task<byte[]> ReadSecretAsync(string kind, string group, string id, byte[] protectedValue)
        {
            var bytes = await secretProtector.UnprotectAsync(protectedValue, SecretPurpose(group, kind, id), cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 32)
                return bytes;
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("A group secret is not 32 bytes.");
        }
    }

    /// <summary>Incrementally merges this component's logical backup records.</summary>
    /// <remarks>
    /// <para>Each completed record is committed. Failure or cancellation retains earlier records;
    /// retrying the stream merges them without duplicates.</para>
    /// <para>Committed business changes raise existing component events after the write lock is released.
    /// Import performs no network I/O and does not replay message processing.</para>
    /// </remarks>
    /// <param name="records">Complete JSON records with <c>type</c> and <c>data</c> fields, in SDK backup order. The caller must ensure they belong to this account and network, and keep their documents alive while records are processed or supply cloned elements.</param>
    /// <param name="cancellationToken">Cancels import; component disposal also cancels it.</param>
    /// <returns>Counts for committed records.</returns>
    public async Task<BackupReport> ImportBackupAsync(IAsyncEnumerable<JsonElement> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var report = new BackupReport();
        var blockedGroups = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var frame = ValidateRecord(record, "group", "group_event", "group_nickname", "group_epoch", "group_secret");
            var notifications = new List<Action>();
            await _groupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var committed = new BackupReport();
            try
            {
                await using var database = new MeshlineDbContext(databaseOptions);
                await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false);
                await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
                await ImportBackupRecordAsync(database, frame, notifications, blockedGroups, committed, cancellationToken).ConfigureAwait(false);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _groupGate.Release(); }
            report.Add(committed);
            foreach (var notification in notifications)
                BackgroundFailure.Notify(notification);
        }
        return report;
    }

    async Task ImportBackupRecordAsync(MeshlineDbContext database, (string Type, JsonElement Data) frame, List<Action> notifications, HashSet<string> blockedGroups, BackupReport report, CancellationToken token)
    {
        var data = frame.Data;
        switch (frame.Type)
        {
            case "group":
                {
                    RequireFields(data, "group_id relay_id locally_closed");
                    var value = Read<GroupData>(data);
                    Check(Identifiers.ValidateGroupId(value.GroupId));
                    Check(RelayIdentity.ValidateRelayId(value.RelayId));
                    var current = await database.Groups.FindAsync([value.GroupId], token).ConfigureAwait(false);
                    if (current is null)
                    {
                        database.Groups.Add(new()
                        {
                            GroupId = value.GroupId,
                            RelayId = value.RelayId,
                            LocallyClosed = value.LocallyClosed
                        });
                        report.Applied++;
                    }
                    else if (current.RelayId != value.RelayId)
                    {
                        blockedGroups.Add(value.GroupId);
                        report.Conflicts++;
                    }
                    else
                        report.Unchanged++;
                    return;
                }
            case "group_nickname":
                {
                    RequireFields(data, "group_id account_id joined_at_sequence sequence nickname");
                    var value = Read<NicknameData>(data);
                    if (!await GroupAllowedAsync(value.GroupId).ConfigureAwait(false))
                        return;
                    Check(AccountAdapter.ValidateAccountId(value.AccountId));
                    Validate(new GroupMemberNicknameUpdate { Nickname = value.Nickname }, Context);
                    if (value.JoinedAtSequence < 0 || value.Sequence < value.JoinedAtSequence)
                        throw new InvalidDataException("Invalid nickname position.");
                    var member = await database.GroupMembers.FindAsync([value.GroupId, value.AccountId], token).ConfigureAwait(false);
                    if (member is null || member.JoinedAtSequence != value.JoinedAtSequence || member.NicknameSequence > value.Sequence)
                        report.Unchanged++;
                    else if (member.NicknameSequence == value.Sequence)
                    {
                        if (member.Nickname != value.Nickname)
                            report.Conflicts++;
                        else
                            report.Unchanged++;
                    }
                    else
                    {
                        member.Nickname = value.Nickname;
                        member.NicknameSequence = value.Sequence;
                        report.Applied++;
                        NotifyBackupGroupChange(notifications, (await database.Groups.FindAsync([value.GroupId], token).ConfigureAwait(false))!, GroupChangeKind.Nickname);
                    }
                    return;
                }
            case "group_epoch":
                {
                    var value = Read<EpochData>(data);
                    try
                    {
                        if (!await GroupAllowedAsync(value.GroupId, requireGroup: false).ConfigureAwait(false))
                            return;
                        await ImportEpochAsync(database, value, report, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (value.ClientSecret is not null)
                            CryptographicOperations.ZeroMemory(value.ClientSecret);
                        if (value.ApplicationSecret is not null)
                            CryptographicOperations.ZeroMemory(value.ApplicationSecret);
                    }
                    return;
                }
            case "group_event":
                {
                    RequireFields(data, EventFields);
                    var value = Read<GroupEventRecord>(data);
                    var payload = ProtocolModel.FromJson<TypedProtocolModel>(value.PayloadJson) ?? throw new InvalidDataException("Missing group event payload.");
                    if (!await GroupAllowedAsync(value.GroupId).ConfigureAwait(false))
                        return;
                    await ImportBackupGroupEventAsync(database, value, payload, notifications, blockedGroups, report, token).ConfigureAwait(false);
                    return;
                }
            case "group_secret":
                {
                    var value = Read<SecretData>(data);
                    try
                    {
                        if (!await GroupAllowedAsync(value.GroupId, requireGroup: false).ConfigureAwait(false))
                            return;
                        if (value.Secret.Length != 32)
                            throw new InvalidDataException("A group secret must contain 32 bytes.");
                        await ImportMemberSecretAsync(database, value, report, token).ConfigureAwait(false);
                    }
                    finally { CryptographicOperations.ZeroMemory(value.Secret); }
                    return;
                }
            default:
                throw new InvalidDataException("Unsupported component backup record.");
        }
        async Task<bool> GroupAllowedAsync(string groupId, bool requireGroup = true)
        {
            Check(Identifiers.ValidateGroupId(groupId));
            if (blockedGroups.Contains(groupId))
            {
                report.Unchanged++;
                return false;
            }
            if (requireGroup && !await database.Groups.AnyAsync(value => value.GroupId == groupId, token).ConfigureAwait(false))
                throw new InvalidDataException("Group data precedes its group reference.");
            return true;
        }
    }

    async Task ImportBackupGroupEventAsync(MeshlineDbContext database, GroupEventRecord value, TypedProtocolModel payload,
        List<Action> notifications, HashSet<string> blockedGroups, BackupReport report, CancellationToken token)
    {
        if (value.Sequence < 0 || value.Epoch < 0)
            throw new InvalidDataException("Invalid group event position.");
        var certificate = value.Rejection is null ? ValidateBackupGroupEvent(value, payload) : null;
        if (value.Rejection is not null && (payload is not GroupMessageEnvelope || value.IsMessage || value.DecryptedPayloadJson is not null))
            throw new InvalidDataException("Only rejected message envelopes can bypass content verification.");
        var group = (await database.Groups.FindAsync([value.GroupId], token).ConfigureAwait(false))!;
        var current = await database.GroupEvents.FindAsync([value.GroupId, value.Sequence], token).ConfigureAwait(false);
        if (current is not null && (current.PayloadJson != value.PayloadJson || current.Epoch != value.Epoch
            || current.DecryptedPayloadJson is not null && value.DecryptedPayloadJson is not null && current.DecryptedPayloadJson != value.DecryptedPayloadJson))
        {
            report.Conflicts++;
            blockedGroups.Add(value.GroupId);
            return;
        }
        if (value.Sequence <= group.Sequence)
        {
            if (current is not null)
            {
                if (current.Rejection is null && current.DecryptedPayloadJson is null && value.DecryptedPayloadJson is not null)
                {
                    current.DecryptedPayloadJson = value.DecryptedPayloadJson;
                    current.IsMessage = value.IsMessage;
                    report.Applied++;
                    NotifyBackupGroupMessage(notifications, group, current);
                }
                else
                    report.Unchanged++;
                return;
            }
            // Existing verified management history must contain every management event.
            // Historical chat can be absent because a previous import excluded it.
            if (payload is not GroupMessageEnvelope)
            {
                report.Conflicts++;
                blockedGroups.Add(value.GroupId);
                return;
            }
            database.GroupEvents.Add(value);
            NotifyBackupGroupMessage(notifications, group, value);
        }
        else
        {
            if (value.Rejection is not null && value.CertificateJson is { } certificateJson)
            {
                certificate = ProtocolModel.FromJson<DeviceCertificate>(certificateJson);
                if (certificate is not null)
                    Validate(certificate, Context);
            }
            var members = await database.GroupMembers.Where(row => row.GroupId == group.GroupId).ToDictionaryAsync(row => row.AccountId, StringComparer.Ordinal, token).ConfigureAwait(false);
            var bans = await database.GroupBans.Where(row => row.GroupId == group.GroupId).ToDictionaryAsync(row => row.AccountId, StringComparer.Ordinal, token).ConfigureAwait(false);
            // Accepted backup events were verified before comparing retained history. Rejected
            // records still use the normal synchronization checks when advancing the timeline.
            var change = await ApplyTimelineEventAsync(database, group, members, bans, value, payload, certificate, notifications, token,
                signatureVerified: value.Rejection is null).ConfigureAwait(false);
            if (change != 0)
                NotifyBackupGroupChange(notifications, group, change);
        }
        report.Applied++;
    }

    void NotifyBackupGroupChange(List<Action> notifications, GroupRecord group, GroupChangeKind change)
    {
        var snapshot = GroupSnapshot(group);
        notifications.Add(() => GroupChanged?.Invoke(this, new(snapshot, change)));
    }

    void NotifyBackupGroupMessage(List<Action> notifications, GroupRecord group, GroupEventRecord record)
    {
        if (!record.IsMessage)
            return;
        var message = MessageSnapshot(new()
        {
            GroupId = group.GroupId,
            RelayId = group.RelayId
        }, record);
        notifications.Add(() => TimelineChanged?.Invoke(this, new([message])));
    }

    async Task ImportEpochAsync(MeshlineDbContext database, EpochData value, BackupReport report, CancellationToken token)
    {
        if (value.Epoch < 0 || value.MemberPublicKey is { Length: not 32 }
            || value.ClientSecret is { Length: not 32 } || value.ApplicationSecret is { Length: not 32 })
            throw new InvalidDataException("Invalid group epoch or secret length.");
        if (value.ClientSecret is { } client && Commitment(value.GroupId, client) != value.Commitment)
            throw new InvalidDataException("The client secret does not match its commitment.");
        var current = await database.GroupEpochs.FindAsync([value.GroupId, value.Epoch], token).ConfigureAwait(false);
        if (current is not null && (current.Commitment is not null && value.Commitment is not null && current.Commitment != value.Commitment
            || current.MemberPublicKey is not null && value.MemberPublicKey is not null && !current.MemberPublicKey.AsSpan().SequenceEqual(value.MemberPublicKey)))
        {
            report.Conflicts++;
            return;
        }
        var commitment = current?.Commitment ?? value.Commitment;
        var memberKey = current?.MemberPublicKey ?? value.MemberPublicKey;
        var epochId = value.Epoch.ToString(CultureInfo.InvariantCulture);
        // Verify the whole logical record before filling any missing field. Never fan out to other epochs.
        if (!await BackupSecretMatchesAsync(current?.ProtectedClientSecret, value.ClientSecret, value.GroupId, "client", commitment!, token).ConfigureAwait(false)
            || !await BackupSecretMatchesAsync(current?.ProtectedApplicationSecret, value.ApplicationSecret, value.GroupId, "epoch", epochId, token).ConfigureAwait(false))
        {
            report.Conflicts++;
            return;
        }
        var changed = current is null || current.Commitment != commitment || !(current.MemberPublicKey ?? []).AsSpan().SequenceEqual(memberKey ?? [])
            || current.ProtectedClientSecret is null && value.ClientSecret is not null
            || current.ProtectedApplicationSecret is null && value.ApplicationSecret is not null;
        if (current is null)
            database.GroupEpochs.Add(current = new()
            {
                GroupId = value.GroupId,
                Epoch = value.Epoch
            });
        current.Commitment = commitment;
        current.MemberPublicKey = memberKey;
        if (current.ProtectedClientSecret is null && value.ClientSecret is not null)
            current.ProtectedClientSecret = await secretProtector.ProtectAsync(value.ClientSecret, SecretPurpose(value.GroupId, "client", commitment!), token).ConfigureAwait(false);
        if (current.ProtectedApplicationSecret is null && value.ApplicationSecret is not null)
            current.ProtectedApplicationSecret = await ProtectEpochAsync(value.GroupId, value.Epoch, value.ApplicationSecret, token).ConfigureAwait(false);
        if (changed)
            report.Applied++;
        else
            report.Unchanged++;
    }

    async Task ImportMemberSecretAsync(MeshlineDbContext database, SecretData value, BackupReport report, CancellationToken token)
    {
        if (value.Kind != "member")
            throw new InvalidDataException("Unsupported group secret kind.");
        var publicKey = new byte[32];
        X25519.GeneratePublicKey(value.Secret, publicKey);
        if (Base64Url.EncodeToString(publicKey) != value.Id)
            throw new InvalidDataException("The private member key does not match its public identifier.");
        var current = await database.GroupMemberKeys.FindAsync([value.GroupId, value.Id], token).ConfigureAwait(false);
        if (current is null)
        {
            var protectedKey = await secretProtector.ProtectAsync(value.Secret, SecretPurpose(value.GroupId, "member", value.Id), token).ConfigureAwait(false);
            database.GroupMemberKeys.Add(new()
            {
                GroupId = value.GroupId,
                PublicKey = value.Id,
                ProtectedPrivateKey = protectedKey
            });
            report.Applied++;
        }
        else if (!await BackupSecretMatchesAsync(current.ProtectedPrivateKey, value.Secret, value.GroupId, "member", value.Id, token).ConfigureAwait(false))
            report.Conflicts++;
        else
            report.Unchanged++;
    }

    async Task<bool> BackupSecretMatchesAsync(byte[]? current, byte[]? incoming, string groupId, string kind, string id, CancellationToken token)
    {
        if (current is null || incoming is null)
            return true;
        var existing = await secretProtector.UnprotectAsync(current, SecretPurpose(groupId, kind, id), token).ConfigureAwait(false);
        try
        {
            return CryptographicOperations.FixedTimeEquals(existing, incoming);
        }
        finally { CryptographicOperations.ZeroMemory(existing); }
    }

    DeviceCertificate? ValidateBackupGroupEvent(GroupEventRecord value, TypedProtocolModel payload)
    {
        BackupData.Validate(payload, Context);
        if (payload is GroupKeyRotated)
        {
            if (value.CertificateJson is not null || value.IsMessage || value.DecryptedPayloadJson is not null)
                throw new InvalidDataException("Invalid relay rotation metadata.");
            return null;
        }
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(value.CertificateJson ?? throw new InvalidDataException("Missing group event certificate."))!;
        BackupData.Validate(certificate, Context);
        var groupId = payload switch
        {
            GroupCreate create => create.GroupId,
            GroupManagementOperation operation => operation.GroupId,
            GroupMessageEnvelope envelope => envelope.GroupId,
            _ => throw new InvalidDataException("Unsupported group history payload.")
        };
        if (groupId != value.GroupId)
            throw new InvalidDataException("Invalid group history signature or binding.");
        try
        {
            VerifySignature(payload, certificate);
        }
        catch (CryptographicException) { throw new InvalidDataException("Invalid group history signature or binding."); }
        if (payload is GroupMessageEnvelope message)
        {
            if (message.Epoch != value.Epoch || message.MessageId != value.MessageId || certificate.Account != value.Sender
                || certificate.GetDeviceId(Context) != value.SenderDeviceId || DateTimeOffset.FromUnixTimeSeconds(message.CreatedAt) != value.CreatedAt)
                throw new InvalidDataException("Invalid group message metadata.");
            var plaintext = value.DecryptedPayloadJson is { } json ? ProtocolModel.FromJson<TypedProtocolModel>(json) : null;
            if (plaintext is not null)
                BackupData.Validate(plaintext, Context);
            if (value.IsMessage != (plaintext is GroupMessage))
                throw new InvalidDataException("Invalid group message history classification.");
        }
        else if (value.IsMessage || value.DecryptedPayloadJson is not null)
            throw new InvalidDataException("Management events cannot contain chat history.");
        return certificate;
    }
}
