using Meshline.Identity;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace Meshline.Components;

sealed partial class MessageManager
{
    const int SendHistoryLimit = 1000;
    readonly SemaphoreSlim _databaseGate = new(1, 1);
    readonly Channel<bool> _sendRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly Channel<bool> _syncRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    Task _sender = Task.CompletedTask;
    Task _synchronizer = Task.CompletedTask;
    Task _poller = Task.CompletedTask;

    void Wake()
    {
        _sendRequests.Writer.TryWrite(true);
        _syncRequests.Writer.TryWrite(true);
    }

    async Task TransactAsync(Func<MeshlineDbContext, List<Action>, CancellationToken, Task> action, CancellationToken cancellationToken) =>
        PublishNotifications(await CommitAsync(action, cancellationToken).ConfigureAwait(false));

    async Task<List<Action>> CommitAsync(Func<MeshlineDbContext, List<Action>, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var notifications = new List<Action>();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await action(database, notifications, cancellationToken).ConfigureAwait(false);
            foreach (var entry in database.ChangeTracker.Entries<MessageOutboxRecord>().Where(value => value.State is EntityState.Added or EntityState.Modified).ToArray())
            {
                await ProcessContactSendChangeAsync(database, entry.Entity, notifications, cancellationToken).ConfigureAwait(false);
            }
            var messagesAdded = database.ChangeTracker.Entries<StoredMessageRecord>().Any(value => value.State == EntityState.Added);
            var sendStatuses = database.ChangeTracker.Entries<MessageOutboxRecord>()
                .Where(value => value.State is EntityState.Added or EntityState.Modified).Select(value => ToStatus(value.Entity)).ToArray();
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await PruneSendHistoryAsync(database, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            // Complete waits from committed snapshots, even if retention removed the record or an application observer throws.
            foreach (var status in sendStatuses) SendStatusCommitted?.Invoke(status);
            if (messagesAdded) notifications.Insert(0, OnTimelineChanged);
        }
        finally { _databaseGate.Release(); }
        return notifications;
    }

    Task<int> PruneSendHistoryAsync(MeshlineDbContext database, CancellationToken cancellationToken) =>
        database.MessageOutbox.Where(value => value.State == MessageSendState.TargetAccepted || value.State == MessageSendState.Failed || value.State == MessageSendState.Canceled)
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.MessageId)
            .Skip(SendHistoryLimit).ExecuteDeleteAsync(cancellationToken);

    void PublishNotifications(List<Action> notifications)
    {
        _sendRequests.Writer.TryWrite(true);
        if (notifications.RemoveAll(notification => notification == OnTimelineChanged) > 0) OnTimelineChanged();
        foreach (var notification in notifications) notification();
    }

    internal async Task SendPayloadAsync(string recipient, TypedProtocolModel payload, CancellationToken cancellationToken, IReadOnlyList<string>? recipientDeviceIds = null)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await RefreshSendStateAsync(recipient, null, cancellationToken).ConfigureAwait(false);
        await TransactAsync(async (database, notifications, token) =>
        {
            await EnqueueAsync(database, notifications, recipient, payload, token, recipientDeviceIds: recipientDeviceIds).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    async Task<MessageOutboxRecord> EnqueueAsync(MeshlineDbContext database, List<Action> notifications, string recipient, TypedProtocolModel payload, CancellationToken cancellationToken, TypedProtocolModel? authorization = null, IReadOnlyList<string>? recipientDeviceIds = null)
    {
        if (AccountAdapter.ValidateAccountId(recipient) is { } accountViolation) throw new ArgumentException(accountViolation.Message, nameof(recipient));
        if (payload.Validate(Options.Context) is { } violation) throw new ArgumentException(violation.Message, nameof(payload));
        var certificate = deviceManager.Local ?? throw new InvalidOperationException("No local device has been created.");
        var routeRecord = await database.AccountRoutes.FindAsync([Options.AccountId], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account has no published home relay route.");
        var route = ProtocolModel.FromJson<AccountRoute>(routeRecord.DocumentJson)!;
        if (route.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()) throw new InvalidOperationException("The account route has expired.");
        var ownRecord = await database.DeviceStates.FindAsync([Options.AccountId], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account device state is unavailable.");
        var own = ProtocolModel.FromJson<AccountDeviceState>(ownRecord.DocumentJson)!;
        if (own.ValidateDeviceAuthorization(certificate.GetDeviceId(Context), Context) is { } localViolation) throw new UnauthorizedAccessException(localViolation.Message);
        authorization ??= await ReadAuthorizationAsync(database, recipient, cancellationToken).ConfigureAwait(false);
        if (payload is DirectMessage && recipient != Options.AccountId && authorization is not ContactGrant)
            throw new UnauthorizedAccessException("A direct message requires a contact grant from its recipient.");
        var recipientRecord = recipient == Options.AccountId ? ownRecord : await database.DeviceStates.FindAsync([recipient], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The recipient device state is unavailable.");
        var recipients = CurrentCertificates(ProtocolModel.FromJson<AccountDeviceState>(recipientRecord.DocumentJson)!);
        if (recipientDeviceIds is not null)
        {
            recipients = recipients.Where(value => recipientDeviceIds.Contains(value.GetDeviceId(Context), StringComparer.Ordinal)).ToArray();
            if (recipients.Length != recipientDeviceIds.Distinct(StringComparer.Ordinal).Count())
                throw new InvalidOperationException("A requested recipient device is not currently authorized.");
        }
        var now = DateTimeOffset.FromUnixTimeSeconds(Clock.UtcNow.ToUnixTimeSeconds());
        var messageId = Identifiers.CreateMessageId();
        var request = await EncryptAsync(messageId, now.ToUnixTimeSeconds(), recipient, payload, recipients, CurrentCertificates(own), authorization, cancellationToken).ConfigureAwait(false);
        var record = new MessageOutboxRecord
        {
            MessageId = messageId,
            Recipient = recipient,
            CreatedAt = now,
            RelayId = route.RelayId,
            RequestJson = request.ToJson(),
            State = MessageSendState.Queued,
            NextAttemptAt = now,
            IsDirect = payload is DirectMessage
        };
        database.MessageOutbox.Add(record);
        if (payload is DirectMessage)
        {
            var message = new StoredMessageRecord { Sender = Options.AccountId, MessageId = messageId, SenderDeviceId = certificate.GetDeviceId(Context), Recipient = recipient, CreatedAt = now, PayloadType = payload.Type, PayloadJson = payload.ToJson(), IsDirect = true };
            database.Messages.Add(message);
        }
        notifications.Add(() => OnSendChanged(record));
        return record;
    }

    async Task RefreshSendStateAsync(string recipient, TypedProtocolModel? authorization, CancellationToken cancellationToken)
    {
        var ownState = RefreshOwnStateAsync();
        if (recipient == Options.AccountId)
        {
            await ownState.ConfigureAwait(false);
            return;
        }
        // Both lookups include verification and persistence. Drain both before enqueueing or
        // propagating failure, including synchronous validation errors captured by the async helpers.
        await Task.WhenAll(ownState, RefreshRecipientStateAsync()).ConfigureAwait(false);

        async Task RefreshOwnStateAsync()
        {
            _ = await deviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account device state is unavailable.");
        }

        async Task RefreshRecipientStateAsync()
        {
            var state = authorization switch
            {
                ContactGrant grant => await deviceManager.GetDeviceStateAsync(grant, cancellationToken).ConfigureAwait(false),
                ContactInvite invite => await deviceManager.GetDeviceStateAsync(invite, cancellationToken).ConfigureAwait(false),
                _ => await deviceManager.GetDeviceStateAsync(recipient, cancellationToken).ConfigureAwait(false)
            };
            if (state is null) throw new InvalidOperationException("The recipient device state is unavailable.");
        }
    }

    static bool IsSecretPayload(TypedProtocolModel payload) => payload is AccountGroupPrivateStateSync or AccountGroupHistorySecretSync;

    async Task<bool> CancelAsync(string messageId, CancellationToken cancellationToken)
    {
        var canceled = false;
        await TransactAsync(async (database, notifications, token) =>
        {
            var record = await database.MessageOutbox.FindAsync([messageId], token).ConfigureAwait(false);
            if (record is not { State: MessageSendState.Queued }) return;
            record.State = MessageSendState.Canceled;
            record.ErrorMessage = null;
            canceled = true;
            notifications.Add(() => OnSendChanged(record));
        }, cancellationToken).ConfigureAwait(false);
        return canceled;
    }

    async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), Clock.Provider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) Wake();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _sendRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_sendRequests.Reader.TryRead(out _)) { }
                try
                {
                    string[] pending;
                    var now = Clock.UtcNow;
                    await using (var database = new MeshlineDbContext(databaseOptions))
                        pending = await database.MessageOutbox.AsNoTracking().Where(value => (value.State == MessageSendState.Queued || value.State == MessageSendState.SubmissionUnknown || value.State == MessageSendState.RelayAccepted) && value.NextAttemptAt <= now)
                            .OrderBy(value => value.CreatedAt).Select(value => value.MessageId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var messageId in pending) await ProcessSendAsync(messageId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.SendMessage, null, exception); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    async Task ProcessSendAsync(string messageId, CancellationToken cancellationToken)
    {
        MessageOutboxRecord? record;
        await using (var database = new MeshlineDbContext(databaseOptions))
            record = await database.MessageOutbox.AsNoTracking().SingleOrDefaultAsync(value => value.MessageId == messageId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.State is not (MessageSendState.Queued or MessageSendState.SubmissionUnknown or MessageSendState.RelayAccepted)) return;
        var mayHaveBeenAccepted = record.State is MessageSendState.SubmissionUnknown or MessageSendState.RelayAccepted;
        try
        {
            var route = record.State == MessageSendState.RelayAccepted ? null : await accountManager.GetRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account has no published home relay route.");
            var relayId = record.State == MessageSendState.Queued ? route!.RelayId : record.RelayId;
            var relay = await relayClients.GetAsync(relayId, deviceManager, cancellationToken).ConfigureAwait(false);
            if (record.State == MessageSendState.Queued)
            {
                if (accountManager.Route?.RelayId != relayId)
                    throw new InvalidOperationException("The home relay changed while preparing the send. The queued message will use the new route on its next attempt.");
                MessageOutboxRecord? claimed = null;
                await TransactAsync(async (database, notifications, token) =>
                {
                    var current = await database.MessageOutbox.FindAsync([messageId], token).ConfigureAwait(false);
                    if (current?.State != MessageSendState.Queued) return;
                    current.RelayId = relayId;
                    current.State = MessageSendState.Submitting;
                    claimed = current;
                    notifications.Add(() => OnSendChanged(current));
                }, cancellationToken).ConfigureAwait(false);
                if (claimed is null) return;
                record = claimed;
            }
            var oldRelay = route is not null && record.RelayId != (accountManager.Route ?? route).RelayId;
            if (oldRelay && record.State == MessageSendState.Submitting)
            {
                record.State = MessageSendState.Queued;
                record.NextAttemptAt = Clock.UtcNow;
                await SaveSendAsync(record, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            MessageDeliveryStatus delivery;
            if (record.State == MessageSendState.RelayAccepted || oldRelay)
                delivery = await relay.SendHttpAsync<MessageDeliveryStatus>(HttpMethod.Get, "message.delivery.status", new MessageDeliveryQuery { MessageId = messageId }, cancellationToken: cancellationToken).ConfigureAwait(false);
            else
                delivery = await relay.SendHttpAsync<MessageDeliveryStatus>(HttpMethod.Post, "message.send", ProtocolModel.FromJson<MessageSendRequest>(record.RequestJson)!, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (record.AcceptedAt is { } acceptedAt && acceptedAt.ToUnixTimeSeconds() != delivery.AcceptedAt)
                throw new InvalidDataException("The relay changed the original message acceptance time.");
            record.State = delivery.Status switch { MessageDeliveryState.Delivering => MessageSendState.RelayAccepted, MessageDeliveryState.TargetAccepted => MessageSendState.TargetAccepted, MessageDeliveryState.Failed => MessageSendState.Failed, _ => throw new InvalidDataException("The delivery status is unknown.") };
            record.AcceptedAt = DateTimeOffset.FromUnixTimeSeconds(delivery.AcceptedAt);
            record.ErrorMessage = delivery.Error?.Message;
            record.NextAttemptAt = Clock.UtcNow.AddSeconds(15);
        }
        catch (Exception exception)
        {
            if (record.State == MessageSendState.Queued)
            {
                await TransactAsync(async (database, notifications, token) =>
                {
                    var current = await database.MessageOutbox.FindAsync([messageId], token).ConfigureAwait(false);
                    if (current?.State != MessageSendState.Queued) return;
                    current.ErrorMessage = exception.Message;
                    current.NextAttemptAt = Clock.UtcNow.AddSeconds(15);
                    notifications.Add(() => OnSendChanged(current));
                }, CancellationToken.None).ConfigureAwait(false);
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                ReportBackgroundError(BackgroundOperation.SendMessage, messageId, exception);
                return;
            }
            var wasAccepted = record.AcceptedAt is not null;
            var definitive = !mayHaveBeenAccepted && exception is RelayException rejection && rejection.Error.IsDefinitiveRejection();
            record.State = wasAccepted ? MessageSendState.RelayAccepted : definitive ? MessageSendState.Failed : MessageSendState.SubmissionUnknown;
            record.ErrorMessage = exception.Message;
            record.NextAttemptAt = Clock.UtcNow.AddSeconds(15);
            await SaveSendAsync(record, CancellationToken.None).ConfigureAwait(false);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            ReportBackgroundError(BackgroundOperation.SendMessage, messageId, exception);
            return;
        }
        await SaveSendAsync(record, CancellationToken.None).ConfigureAwait(false);
    }

    Task SaveSendAsync(MessageOutboxRecord record, CancellationToken cancellationToken) =>
        TransactAsync((database, notifications, _) =>
        {
            database.MessageOutbox.Update(record);
            notifications.Add(() => OnSendChanged(record));
            return Task.CompletedTask;
        }, cancellationToken);

    async Task<TypedProtocolModel?> ReadAuthorizationAsync(MeshlineDbContext database, string recipient, CancellationToken cancellationToken)
    {
        if (recipient == Options.AccountId) return null;
        var contact = await database.Contacts.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == recipient && value.State == ContactRelationshipState.Active, cancellationToken).ConfigureAwait(false);
        if (contact?.GrantFromJson is { } json) return ProtocolModel.FromJson<ContactGrant>(json);
        var request = await database.ContactRequests.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == recipient && value.Direction == ContactRequestDirection.Incoming, cancellationToken).ConfigureAwait(false);
        return request is null ? null : ProtocolModel.FromJson<ContactConsent>(request.ConsentJson)!.Grant;
    }

    static DeviceCertificate[] CurrentCertificates(AccountDeviceState state)
    {
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        return state.Certificates.Where(value => value.NotBefore <= now && value.ExpiresAt > now).ToArray();
    }

    static ContactGrant ValidateGrant(ContactGrant grant, AccountDeviceState state, NetworkContext context)
    {
        var authorized = FilterGrantSignatures(grant, state, context);
        if (authorized.Signatures.IsEmpty) throw new InvalidDataException("The contact grant has no currently authorized signing device.");
        return authorized;
    }

    static ContactGrant FilterGrantSignatures(ContactGrant grant, AccountDeviceState state, NetworkContext context)
    {
        if (grant.Validate(context) is { } violation) throw new InvalidDataException(violation.Message);
        if (grant.Grantor != state.Account) throw new InvalidDataException("The contact grant device state belongs to another account.");
        var input = grant.GetSigningInput(context);
        var signatures = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var certificate in CurrentCertificates(state))
        {
            var deviceId = certificate.GetDeviceId(context);
            if (grant.Signatures.TryGetValue(deviceId, out var signature) && Ed25519.Verify(signature.AsSpan(), certificate.SigningPublicKey.AsSpan(), input))
                signatures[deviceId] = signature;
        }
        return grant with { Signatures = signatures.ToImmutable() };
    }

    static MessageSendStatus ToStatus(MessageOutboxRecord record) => new()
    {
        MessageId = record.MessageId,
        Recipient = record.Recipient,
        CreatedAt = record.CreatedAt,
        State = record.State,
        AcceptedRelayId = record.AcceptedAt is null ? null : record.RelayId,
        AcceptedAt = record.AcceptedAt,
        ErrorMessage = record.ErrorMessage
    };

    static MessageInfo ToMessage(StoredMessageRecord record)
    {
        var payload = ProtocolModel.FromJson<DirectMessage>(record.PayloadJson!)!;
        return new()
        {
            LocalSequence = record.LocalSequence,
            Key = new() { Sender = record.Sender, MessageId = record.MessageId },
            SenderDeviceId = record.SenderDeviceId,
            Recipient = record.Recipient,
            CreatedAt = record.CreatedAt,
            Body = payload.Body,
            Attachments = payload.Attachments is { } attachments ? attachments : null,
            ReplyTo = payload.ReplyTo is { } reply ? new() { Sender = reply.From, MessageId = reply.MessageId } : null
        };
    }
}
