using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace Meshline.Components;

sealed partial class GroupManager
{
    delegate Task MessageEffect(MeshlineDbContext database, CancellationToken cancellationToken);

    async Task ProcessAccountMessagesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            long after;
            await using (var database = new MeshlineDbContext(databaseOptions))
                after = (await database.GroupAccountMessageCursors.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false))?.LocalSequence ?? 0;
            var messages = await messageManager.ReadTimelineAsync(after, 64, cancellationToken).ConfigureAwait(false);
            if (messages.Count == 0) return;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MessageEffect? effect = null;
                Exception? rejection = null;
                try { effect = await PrepareAccountMessageAsync(message, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (exception is InvalidDataException or JsonException or DecoderFallbackException) { rejection = exception; }
                try { await CommitAccountMessageAsync(message.LocalSequence, effect, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (rejection is null && exception is InvalidDataException or JsonException or DecoderFallbackException)
                {
                    rejection = exception;
                    await CommitAccountMessageAsync(message.LocalSequence, null, cancellationToken).ConfigureAwait(false);
                }
                if (rejection is not null) ReportBackgroundError(BackgroundOperation.Synchronize, message.MessageId, rejection);
                else if (effect is not null) Wake();
            }
        }
    }

    async Task CommitAccountMessageAsync(long localSequence, MessageEffect? effect, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var cursor = await database.GroupAccountMessageCursors.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (cursor is null) database.GroupAccountMessageCursors.Add(cursor = new() { Id = 1 });
        if (cursor.LocalSequence >= localSequence) return;
        if (effect is not null) await effect(database, cancellationToken).ConfigureAwait(false);
        cursor.LocalSequence = localSequence;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task<MessageEffect?> PrepareAccountMessageAsync(AccountMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        if (payload is not (AccountGroupPrivateStateRequest or AccountGroupPrivateStateSync or AccountGroupHistorySecretSync)) return null;
        if (message.Sender != Options.AccountId || message.Recipient != Options.AccountId) throw new InvalidDataException("Group private state must be synchronized within the current account.");
        if (payload.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
        var own = await deviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The current account device state is unavailable.");
        if (own.ValidateDeviceAuthorization(message.SenderDeviceId, Context) is { } authorization) throw new InvalidDataException(authorization.Message);
        if (payload is AccountGroupPrivateStateRequest request)
        {
            await ReplyToPrivateStateRequestAsync(message.SenderDeviceId, request, cancellationToken).ConfigureAwait(false);
            return null;
        }
        return payload switch
        {
            AccountGroupPrivateStateSync state => await PreparePrivateStatesAsync(state, cancellationToken).ConfigureAwait(false),
            AccountGroupHistorySecretSync history => await PrepareHistorySecretsAsync(history, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException("The group payload type does not match its stored type.")
        };
    }
}
