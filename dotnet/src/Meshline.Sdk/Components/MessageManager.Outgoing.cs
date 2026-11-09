using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Text.Json;

namespace Meshline.Components;

sealed partial class MessageManager
{
    static readonly Expression<Func<MessageOutboxRecord, OutgoingMessageInfo>> OutgoingMessageProjection = record => new()
    {
        Message = ReadOutgoingMessage(record.PayloadJson),
        Status = new MessageSendStatus
        {
            MessageId = record.MessageId,
            Recipient = record.Recipient,
            CreatedAt = record.CreatedAt,
            State = record.State,
            AcceptedRelayId = record.AcceptedAt == null ? null : record.RelayId,
            AcceptedAt = record.AcceptedAt,
            ErrorMessage = record.ErrorMessage
        }
    };

    static DirectMessage ReadOutgoingMessage(string? json) =>
        ProtocolModel.FromJson<DirectMessage>(json ?? throw new InvalidDataException("The direct-message outbox entry has no stored content."))
            ?? throw new InvalidDataException("The direct-message outbox content is null.");

    /// <summary>
    /// Reads the content and send status of one retained outgoing direct message.
    /// </summary>
    /// <param name="messageId">The outgoing message identifier.</param>
    /// <param name="cancellationToken">Cancels the local read.</param>
    /// <returns>The complete outgoing message, or null if the direct-message outbox entry is absent or has been evicted.</returns>
    /// <remarks>Content and status are read together from local storage, as in <see cref="GetOutboxAsync"/>.
    /// Direct outbox messages have no local history sequence. Internal protocol messages are excluded.</remarks>
    /// <exception cref="ArgumentException">The message identifier is invalid.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">The component has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The call or component lifetime is canceled.</exception>
    /// <exception cref="SqliteException">Local storage cannot be accessed.</exception>
    /// <exception cref="InvalidDataException">The direct-message outbox entry is missing its required content.</exception>
    /// <exception cref="JsonException">The stored content violates protocol JSON representation rules.</exception>
    public async Task<OutgoingMessageInfo?> GetOutgoingMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (Identifiers.ValidateMessageId(messageId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(messageId));
        await using var database = new MeshlineDbContext(databaseOptions);
        return await database.MessageOutbox.AsNoTracking()
            .Where(value => value.MessageId == messageId && value.IsDirect)
            .Select(OutgoingMessageProjection).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a snapshot reader for the content and send status of direct-message outbox entries matching the recipient and state filters.
    /// </summary>
    /// <param name="recipient">An optional recipient account filter; <see langword="null"/> includes all recipients.</param>
    /// <param name="state">The outbox states to include; flags may be combined and the default includes all states.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// <para>Each result contains plaintext content and send status from the same local snapshot, ordered by creation
    /// time and then ordinal message identifier ascending. Outbox entries have no local history sequence.
    /// Open a new reader to observe later changes; consuming a record with missing or invalid content raises an error.</para>
    /// <para>Results include retained terminal records. The SDK retains the newest 1000 eligible terminal records
    /// across the account's entire outbox, including internal protocol messages. Source-accepted direct messages
    /// without synchronized history remain protected from eviction. This query returns direct messages only.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller or component lifetime cancels the local read.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="ArgumentException">The send-state filter contains unsupported flags.</exception>
    public async Task<QueryReader<OutgoingMessageInfo>> GetOutboxAsync(string? recipient = null, MessageSendState state = MessageSendState.All, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if ((state & ~MessageSendState.All) != 0)
            throw new ArgumentException("The query contains an unsupported send state.", nameof(state));
        return await QueryReader<OutgoingMessageInfo>.OpenAsync(databaseOptions, database =>
        {
            var records = database.MessageOutbox.AsNoTracking().Where(value => value.IsDirect);
            if (recipient is not null)
                records = records.Where(value => value.Recipient == recipient);
            if (state != MessageSendState.All)
                records = records.Where(value => (value.State & state) != 0);
            return records.OrderBy(value => value.CreatedAt).ThenBy(value => value.MessageId)
                .Select(OutgoingMessageProjection);
        }, cancellationToken).ConfigureAwait(false);
    }
}
