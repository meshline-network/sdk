using Meshline.Backups;
using Meshline.Identity;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static Meshline.Backups.BackupData;

namespace Meshline.Components;

sealed partial class MessageManager
{
    /// <summary>Exports this component's portable logical records from a consistent local snapshot.</summary>
    /// <remarks>Records are plaintext. Use <see cref="MeshlineClient"/> for encrypted backup files. This operation does not access the network.</remarks>
    /// <param name="history">Optional private conversation history selection; defaults to <see cref="BackupSelection.None"/>. Base data is always exported.</param>
    /// <param name="cancellationToken">Cancels enumeration; component disposal also cancels it.</param>
    /// <returns>Complete JSON records with <c>type</c> and <c>data</c> fields, independent of internal documents and valid after enumeration ends.</returns>
    public async IAsyncEnumerable<JsonElement> ExportBackupAsync(BackupSelection? history = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        history ??= BackupSelection.None;
        if (!ValidSelection(history, group: false, cancellationToken))
            throw new ArgumentException("Invalid history selection.", nameof(history));
        await using var database = new MeshlineDbContext(databaseOptions);
        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await foreach (var contact in database.Contacts.AsNoTracking().OrderBy(value => value.AccountId).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var record = new ContactRecord
            {
                Account = contact.AccountId,
                Alias = contact.Alias,
                Status = contact.State,
                UpdatedAt = contact.UpdatedAt.ToUnixTimeSeconds(),
                GrantFromContact = contact.State == ContactRelationshipState.Active && contact.GrantFromJson is { } incoming ? ProtocolModel.FromJson<ContactGrant>(incoming) : null,
                GrantToContact = contact.State == ContactRelationshipState.Active && contact.GrantToJson is { } outgoing ? ProtocolModel.FromJson<ContactGrant>(outgoing) : null
            };
            yield return Record("contact", Element(record.ToJson()));
        }
        if (history.Mode != "none")
        {
            var messages = database.Messages.AsNoTracking().Where(value => value.IsDirect && (value.Sender == Options.AccountId || value.Recipient == Options.AccountId));
            if (history.Mode == "only")
            {
                var peers = history.Ids.ToArray();
                messages = messages.Where(value => peers.Contains(value.Sender == Options.AccountId ? value.Recipient : value.Sender));
            }
            await foreach (var message in messages.OrderBy(value => value.LocalSequence).AsAsyncEnumerable().WithCancellation(cancellationToken))
                yield return Record("message", new MessageData(message.Sender, message.MessageId, message.SenderDeviceId, message.Recipient,
                    message.CreatedAt.ToUnixTimeSeconds(), Element(message.PayloadJson!)));
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
        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var frame = ValidateRecord(record, "contact", "message");
            var notifications = new List<Action>();
            await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var committed = new BackupReport();
            try
            {
                await using var database = new MeshlineDbContext(databaseOptions);
                await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false);
                await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
                await ImportBackupRecordAsync(database, frame, notifications, committed, cancellationToken).ConfigureAwait(false);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _databaseGate.Release(); }
            report.Add(committed);
            foreach (var notification in notifications)
                BackgroundFailure.Notify(notification);
        }
        return report;
    }

    Task ImportBackupRecordAsync(MeshlineDbContext database, (string Type, JsonElement Data) frame, List<Action> notifications, BackupReport report, CancellationToken token) => frame.Type switch
    {
        "contact" => ImportBackupContactAsync(database, Protocol<ContactRecord>(frame.Data), notifications, report, token),
        "message" => ImportBackupMessageAsync(database, Read<MessageData>(frame.Data), notifications, report, token),
        _ => throw new InvalidDataException("Unsupported component backup record.")
    };

    async Task ImportBackupContactAsync(MeshlineDbContext database, ContactRecord value, List<Action> notifications, BackupReport report, CancellationToken token)
    {
        BackupData.Validate(value, Context);
        if (value.Account == Options.AccountId)
            throw new InvalidDataException("A backup contact cannot be the owning account.");
        CheckBackupGrant(value.GrantFromContact, value.Account, Options.AccountId);
        CheckBackupGrant(value.GrantToContact, Options.AccountId, value.Account);
        // Select relationship metadata before considering either direction of authorization.
        var current = await database.Contacts.FindAsync([value.Account], token).ConfigureAwait(false);
        var updated = DateTimeOffset.FromUnixTimeSeconds(value.UpdatedAt);
        if (current is not null && current.UpdatedAt > updated)
        {
            report.Unchanged++;
            return;
        }
        if (current is not null && current.UpdatedAt == updated && (current.State != value.Status || current.Alias != value.Alias))
        {
            report.Conflicts++;
            return;
        }
        // Merge authorization independently of alias changes, using the held device evidence.
        var incoming = value.Status == ContactRelationshipState.Deleted ? null
            : await MergeBackupGrantAsync(database, current?.GrantFromJson, value.GrantFromContact, token).ConfigureAwait(false);
        var outgoing = value.Status == ContactRelationshipState.Deleted ? null
            : await MergeBackupGrantAsync(database, current?.GrantToJson, value.GrantToContact, token).ConfigureAwait(false);
        if (current is not null && current.UpdatedAt == updated && current.GrantFromJson == incoming && current.GrantToJson == outgoing)
        {
            report.Unchanged++;
            return;
        }
        var change = (ContactChangeKind)0;
        if (current is null || current.State != value.Status)
            change |= value.Status == ContactRelationshipState.Deleted ? ContactChangeKind.Deleted : ContactChangeKind.Relationship;
        if (current?.Alias != value.Alias)
            change |= ContactChangeKind.Alias;
        if (current?.GrantFromJson != incoming || current?.GrantToJson != outgoing)
            change |= ContactChangeKind.Authorization;
        if (current is null)
            database.Contacts.Add(current = new()
            {
                AccountId = value.Account
            });
        current.State = value.Status;
        current.Alias = value.Alias;
        current.UpdatedAt = updated;
        current.GrantFromJson = incoming;
        current.GrantToJson = outgoing;
        current.DeviceNotificationRevision = -1;
        // Keep local delivery evidence and device-state hints while the relationship remains active.
        if (value.Status == ContactRelationshipState.Deleted)
        {
            current.ConfirmedGrantToJson = null;
            current.RequiredDeviceRevision = null;
        }
        report.Applied++;
        // Let the normal poller maintain restored contacts after this record commits.
        if (value.Status == ContactRelationshipState.Active)
            notifications.Add(() => Interlocked.Exchange(ref _contactMaintenanceRetryPending, 1));
        if (change != 0)
            NotifyContact(notifications, current, change);
    }

    async Task ImportBackupMessageAsync(MeshlineDbContext database, MessageData value, List<Action> notifications, BackupReport report, CancellationToken token)
    {
        Check(AccountAdapter.ValidateAccountId(value.Sender));
        Check(AccountAdapter.ValidateAccountId(value.Recipient));
        Check(Identifiers.ValidateMessageId(value.MessageId));
        Check(Identifiers.ValidateDeviceId(value.SenderDeviceId));
        if (value.Sender != Options.AccountId && value.Recipient != Options.AccountId)
            throw new InvalidDataException("The message does not belong to the backup account.");
        var payload = Protocol<DirectMessage>(value.Payload);
        BackupData.Validate(payload, Context);
        var created = DateTimeOffset.FromUnixTimeSeconds(value.CreatedAt);
        var current = await database.Messages.SingleOrDefaultAsync(row => row.Sender == value.Sender && row.MessageId == value.MessageId, token).ConfigureAwait(false);
        if (current is not null)
        {
            if (current.Recipient != value.Recipient || current.SenderDeviceId != value.SenderDeviceId || current.CreatedAt != created || current.PayloadJson != payload.ToJson())
                report.Conflicts++;
            else
                report.Unchanged++;
            return;
        }
        var message = new StoredMessageRecord
        {
            Sender = value.Sender,
            Recipient = value.Recipient,
            MessageId = value.MessageId,
            SenderDeviceId = value.SenderDeviceId,
            CreatedAt = created,
            IsDirect = true,
            PayloadType = payload.Type,
            PayloadJson = payload.ToJson()
        };
        database.Messages.Add(message);
        report.Applied++;
        // LocalSequence is assigned by SaveChanges before these callbacks are dispatched.
        notifications.Add(() => OnMessageReceived([ToMessage(message)]));
    }

    async Task<string?> MergeBackupGrantAsync(MeshlineDbContext database, string? previousJson, ContactGrant? incoming, CancellationToken token)
    {
        if (incoming is null)
            return previousJson;
        var incomingJson = incoming.ToJson();
        // A first restore retains held authorization material, even without current verification evidence.
        if (previousJson is null || previousJson == incomingJson)
            return incomingJson;
        var previous = ProtocolModel.FromJson<ContactGrant>(previousJson)!;
        var sameBody = previous.GetSigningInput(Context).AsSpan().SequenceEqual(incoming.GetSigningInput(Context));
        if (!sameBody && (incoming.ExpiresAt ?? long.MaxValue) <= (previous.ExpiresAt ?? long.MaxValue))
            return previousJson;
        var stored = await database.DeviceStates.FindAsync([incoming.Grantor], token).ConfigureAwait(false);
        if (stored is null)
        {
            // Keep unknown signatures as verification material; an unverified duplicate cannot replace a held signature.
            return sameBody ? (incoming with
            {
                Signatures = incoming.Signatures.SetItems(previous.Signatures)
            }).ToJson() : previousJson;
        }
        if (incoming.Validate(Context) is not null)
            return previousJson;
        var state = ProtocolModel.FromJson<AccountDeviceState>(stored.DocumentJson)!;
        var verified = FilterGrantSignatures(incoming, state, Context);
        if (verified.Signatures.IsEmpty)
            return previousJson;
        return MergeGrant(previousJson, verified, state).ToJson();
    }

    void CheckBackupGrant(ContactGrant? grant, string grantor, string grantee)
    {
        if (grant is null)
            return;
        // Expiry remains attached to the restored grant; it is not an import-format failure.
        BackupData.Validate(grant with
        {
            ExpiresAt = null
        }, Context);
        if (grant.ExpiresAt < 0)
            throw new InvalidDataException("Invalid contact grant expiry.");
        if (grant.Grantor != grantor || grant.Grantee != grantee)
            throw new InvalidDataException("Backup contact grant direction is invalid.");
    }
}
