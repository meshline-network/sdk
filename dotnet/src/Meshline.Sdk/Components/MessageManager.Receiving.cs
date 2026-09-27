using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace Meshline.Components;

sealed partial class MessageManager
{
    async Task<List<Action>> ReceiveMessageAsync(string relayId, MessageTimelineEntry entry, DeviceCertificate certificate, bool hasRetentionGap, List<MessageInfo> messages, CancellationToken cancellationToken)
    {
        var envelope = entry.Envelope;
        StoredMessageRecord? record = null;
        AccountDeviceState? own = null;
        ContactMessageEffect? contactEffect = null;
        Exception? rejection = null;
        try
        {
            bool exists;
            await using (var database = new MeshlineDbContext(databaseOptions))
                exists = await database.Messages.AnyAsync(value => value.Sender == envelope.From && value.MessageId == envelope.MessageId, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                if (envelope.CreatedAt < 0 || envelope.CreatedAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) throw new InvalidDataException("The message timestamp is outside the supported range.");
                var payload = await DecryptAsync(envelope, entry.KeyBox, certificate, cancellationToken).ConfigureAwait(false);
                if (envelope.To == Options.AccountId && envelope.From != Options.AccountId && payload is DirectMessage or ContactGrant or DeviceStateChanged)
                {
                    await using var database = new MeshlineDbContext(databaseOptions);
                    _ = await ReadContactAuthorizationAsync(database, envelope.From, cancellationToken).ConfigureAwait(false);
                }
                if (payload is DirectMessage)
                {
                    if (payload.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
                    if (envelope.To == Options.AccountId)
                        own = await deviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("The current account device state is unavailable.");
                }
                record = new()
                {
                    Sender = envelope.From,
                    MessageId = envelope.MessageId,
                    SenderDeviceId = envelope.FromDeviceId,
                    Recipient = envelope.To,
                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(envelope.CreatedAt),
                    PayloadType = payload.Type,
                    PayloadJson = IsSecretPayload(payload) ? null : payload.ToJson(),
                    IsDirect = payload is DirectMessage
                };
                contactEffect = await PrepareContactMessageAsync(record, payload, cancellationToken).ConfigureAwait(false);
                await ProtectPayloadAsync(record, payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or DecoderFallbackException) { rejection = exception; }
        List<Action> notifications;
        try { notifications = await CommitReceivedMessageAsync(record, own, contactEffect, rejection, relayId, entry, hasRetentionGap, messages, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (rejection is null && exception is InvalidDataException or JsonException or DecoderFallbackException)
        {
            notifications = await CommitReceivedMessageAsync(null, null, null, exception, relayId, entry, hasRetentionGap, messages, cancellationToken).ConfigureAwait(false);
        }
        return notifications;
    }

    Task<List<Action>> CommitReceivedMessageAsync(StoredMessageRecord? record, AccountDeviceState? own, ContactMessageEffect? contactEffect, Exception? rejection, string relayId, MessageTimelineEntry entry, bool hasRetentionGap, List<MessageInfo> messages, CancellationToken cancellationToken) =>
        CommitAsync(async (database, notifications, token) =>
        {
            var progress = await database.AccountTimelines.FindAsync([relayId], token).ConfigureAwait(false);
            if (progress is null) database.AccountTimelines.Add(progress = new() { RelayId = relayId });
            if (progress.Sequence >= entry.Sequence) return;
            if (rejection is null && record is not null && !await database.Messages.AnyAsync(value => value.Sender == record.Sender && value.MessageId == record.MessageId, token).ConfigureAwait(false))
            {
                if (record.IsDirect && record.Recipient == Options.AccountId)
                {
                    if (record.Sender == Options.AccountId)
                    {
                        if (own!.ValidateDeviceAuthorization(record.SenderDeviceId, Context) is { } violation) throw new InvalidDataException(violation.Message);
                    }
                    else
                    {
                        var grant = await ReadContactAuthorizationAsync(database, record.Sender, token).ConfigureAwait(false);
                        ValidateGrant(grant, own!, Context);
                    }
                }
                if (contactEffect is not null)
                {
                    await contactEffect(database, notifications, token).ConfigureAwait(false);
                    notifications.Add(() => _refreshRequests.Writer.TryWrite(true));
                }
                database.Messages.Add(record);
                if (record.IsDirect)
                    notifications.Add(() => messages.Add(ToMessage(record)));
            }
            progress.Sequence = entry.Sequence;
            progress.HasRetentionGap |= hasRetentionGap;
            progress.LastSynchronizedAt = Clock.UtcNow;
            if (rejection is not null) notifications.Add(() => ReportBackgroundError(BackgroundOperation.Synchronize, entry.Envelope.MessageId, rejection));
        }, cancellationToken);
}
