using Meshline.Identity;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline;

sealed partial class MeshlineClient
{
    /// <summary>
    /// Opens a snapshot reader for locally available conversations matching the supplied filters.
    /// </summary>
    /// <param name="query">Optional conversation filters; <see langword="null"/> includes all conversation kinds.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="ArgumentException">The query contains unsupported conversation-kind flags.</exception>
    public async Task<QueryReader<Conversation>> GetConversationsAsync(ConversationQuery? query = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (query is not null && (query.Kind & ~ConversationKind.All) != 0)
            throw new ArgumentException("The query contains an unsupported conversation kind.", nameof(query));
        return await QueryReader<Conversation>.OpenAsync(_databaseOptions, database => QueryConversations(database, Options.AccountId, query), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one locally available conversation by its peer, group, or channel identifier.
    /// </summary>
    /// <param name="conversationId">The peer account identifier for a direct conversation, or the group or channel identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The local conversation summary, or <see langword="null"/> when absent.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="ArgumentException">The conversation identifier is not a valid peer account, group, or channel identifier.</exception>
    /// <exception cref="NotSupportedException">A direct-conversation identifier uses an unsupported account namespace.</exception>
    public async Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        _ = GetConversationKind(conversationId);
        await using var database = new MeshlineDbContext(_databaseOptions);
        return await QueryConversations(database, Options.AccountId, conversationId: conversationId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advances a conversation's local read position to its latest currently stored readable message.
    /// </summary>
    /// <param name="conversationId">The peer account identifier for a direct conversation, or the group or channel identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// This overload marks all currently stored readable messages as read, including arrivals since an earlier query.
    /// To acknowledge only messages already viewed, use the overload that accepts their <c>LocalSequence</c>.
    /// An empty conversation is a no-op, and the read position never moves backward.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The conversation kind cannot be handled by the local read-position update.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="ArgumentException">The conversation identifier is not a valid peer account, group, or channel identifier.</exception>
    /// <exception cref="NotSupportedException">A direct-conversation identifier uses an unsupported account namespace.</exception>
    public Task MarkReadAsync(string conversationId, CancellationToken cancellationToken = default) =>
        MarkReadCoreAsync(conversationId, null, cancellationToken);

    /// <summary>
    /// Advances a conversation's local read position through the supplied message position, inclusively.
    /// </summary>
    /// <param name="conversationId">The peer account identifier for a direct conversation, or the group or channel identifier.</param>
    /// <param name="localSequence">The positive <c>LocalSequence</c> of a message the application has read in this conversation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// The position only advances; repeated or older positions are no-ops. New messages beyond this fixed boundary remain unread.
    /// Advancing requires a locally stored direct message, decrypted group message, or known original channel publication at this position.
    /// A channel publication remains a valid boundary after deletion while its original metadata is retained.
    /// This is a cumulative read position, so filtered or incomplete history must not be treated as proof that every earlier message was read.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The conversation kind cannot be handled by the local read-position update.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="ArgumentException">The conversation identifier is not a valid peer account, group, or channel identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The position is not positive, or advancing to it would not identify a locally known message in this conversation.</exception>
    /// <exception cref="NotSupportedException">A direct-conversation identifier uses an unsupported account namespace.</exception>
    public Task MarkReadAsync(string conversationId, long localSequence, CancellationToken cancellationToken = default) =>
        MarkReadCoreAsync(conversationId, localSequence, cancellationToken);

    async Task MarkReadCoreAsync(string conversationId, long? localSequence, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var kind = GetConversationKind(conversationId);
        if (localSequence.HasValue) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(localSequence.Value, nameof(localSequence));
        await using var database = new MeshlineDbContext(_databaseOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sequence = localSequence ?? (kind switch
        {
            ConversationKind.Direct => await database.Messages.Where(value => value.IsDirect &&
                (value.Sender == Options.AccountId && value.Recipient == conversationId || value.Sender == conversationId && value.Recipient == Options.AccountId))
                .MaxAsync(value => (long?)value.LocalSequence, cancellationToken).ConfigureAwait(false),
            ConversationKind.Group => await database.GroupEvents.Where(value => value.GroupId == conversationId && value.IsMessage && value.DecryptedPayloadJson != null)
                .MaxAsync(value => (long?)value.Sequence, cancellationToken).ConfigureAwait(false),
            ConversationKind.Channel => await database.ChannelPosts.Where(value => value.ChannelId == conversationId && !value.IsDeleted && value.PostJson != null)
                .MaxAsync(value => (long?)value.Sequence, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported conversation kind.")
        });
        if (sequence is null) return;
        var record = await database.ConversationReads.FindAsync([conversationId], cancellationToken).ConfigureAwait(false);
        if (record is not null && record.Sequence >= sequence.Value) return;
        if (localSequence.HasValue)
        {
            var known = kind switch
            {
                ConversationKind.Direct => await database.Messages.AnyAsync(value => value.IsDirect && value.LocalSequence == localSequence.Value &&
                    (value.Sender == Options.AccountId && value.Recipient == conversationId || value.Sender == conversationId && value.Recipient == Options.AccountId), cancellationToken).ConfigureAwait(false),
                ConversationKind.Group => await database.GroupEvents.AnyAsync(value => value.GroupId == conversationId && value.Sequence == localSequence.Value && value.IsMessage && value.DecryptedPayloadJson != null, cancellationToken).ConfigureAwait(false),
                ConversationKind.Channel => await database.ChannelPosts.AnyAsync(value => value.ChannelId == conversationId && value.Sequence == localSequence.Value && value.MessageId != null, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported conversation kind.")
            };
            if (!known) throw new ArgumentOutOfRangeException(nameof(localSequence), "The position does not identify a locally known message in this conversation.");
        }
        if (record is null)
            database.ConversationReads.Add(new() { ConversationId = conversationId, Sequence = sequence.Value });
        else
            record.Sequence = sequence.Value;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        QueueConversationChanges([conversationId]);
    }

    static ConversationKind GetConversationKind(string conversationId)
    {
        var kind = conversationId.StartsWith("grp_", StringComparison.Ordinal) ? ConversationKind.Group
            : conversationId.StartsWith("chan_", StringComparison.Ordinal) ? ConversationKind.Channel : ConversationKind.Direct;
        var violation = kind switch
        {
            ConversationKind.Group => Identifiers.ValidateGroupId(conversationId),
            ConversationKind.Channel => Identifiers.ValidateChannelId(conversationId),
            _ => AccountAdapter.ValidateAccountId(conversationId)
        };
        if (violation is not null) throw new ArgumentException(violation.Message ?? "Invalid conversation identifier.", nameof(conversationId));
        return kind;
    }
}
