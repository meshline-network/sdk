# Conversations and unread state

[Guide index](../README.md) · [Storage and pagination](storage-and-pagination.md)

## Build a local conversation list

`MeshlineClient` combines direct messages, followed channels, and groups into one local conversation API. Initialize the database and client first, then keep the session running for new data. A query reports currently stored state; opening a query is not a request to download every conversation's history.

Use `ConversationQuery.Kind`, `UnreadOnly`, and `HasMessages` to filter results. Read the returned `QueryReader<Conversation>` in bounded batches:

<!-- snippet: conversations -->
```csharp
public static async Task PrintUnreadConversationsAsync(
    MeshlineClient client, CancellationToken cancellationToken = default)
{
    await using var reader = await client.GetConversationsAsync(
        new ConversationQuery { UnreadOnly = true }, cancellationToken);
    while (true)
    {
        var batch = await reader.ReadNextAsync(50, cancellationToken);
        if (batch.Count == 0) break;
        foreach (var conversation in batch)
            Console.WriteLine($"{conversation.ConversationId}: {conversation.UnreadCount}");
    }
}

// Pass the last message position the application has actually read.
public static Task MarkConversationReadAsync(
    MeshlineClient client, string conversationId, long localSequence, CancellationToken cancellationToken = default) =>
    client.MarkReadAsync(conversationId, localSequence, cancellationToken);
```
<!-- /snippet -->

Source: [Messaging.cs](../../examples/Meshline.Sdk.Examples/Messaging.cs). The list method only displays unread counts. After the application has actually viewed messages, pass the last acknowledged message's `LocalSequence` to `MarkReadAsync(conversationId, localSequence)`. It updates a local read position; it does not send a recipient read receipt to another account.

## Track read positions

`MarkReadAsync(conversationId, cancellationToken)` accepts an optional cancellation token. It marks through the latest locally stored readable message when the operation executes, so it can include arrivals since an earlier query. Use it for a "mark all as read" action. Empty conversations are no-ops, and the read position never moves backward. Use the explicit `localSequence` overload when confirming a batch the application has viewed.

The boundary is inclusive and cumulative. Acknowledging position 100 leaves messages beyond 100 unread, including messages that arrive between querying and marking or during a write retry. Repeated or older positions are no-ops, and concurrent acknowledgments cannot move the position backward. Positions must be positive. Advancing to a position that is not a locally stored direct message, decrypted group message, or known original channel publication is rejected. A deleted channel post can still be acknowledged while its original publication metadata is retained.

Use positions from the messages actually viewed in one conversation. Reading one batch does not acknowledge later batches, and an empty batch supplies no new boundary. A sender filter or incomplete history does not establish that all earlier messages were viewed. For groups and channels, history backfilled or decrypted later at or below the acknowledged position is part of the already acknowledged prefix.

## Choose the correct position

Message models expose a common `LocalSequence` property for the position used by the conversation's local read state:

| Message model | `LocalSequence` |
| --- | --- |
| `MessageInfo` | The sequence assigned in this database's account-message stream. |
| `GroupMessageInfo` | A read-only alias of `Sequence`, assigned by the hosting relay within that group. |
| `ChannelPostInfo` | A read-only alias of `Ref.Sequence`, the original publication position within that channel. Edits retain this position. |

Keep the conversation identifier with the position. Values may have gaps, and group/channel aliases retain their original timeline scope rather than allocating another local counter.

Direct messages share an account-wide `LocalSequence`, but each peer's conversation stores its own read position. Both `MarkReadAsync` overloads update only the supplied conversation. For example, if messages arrive as A:10, B:20, A:30, B:40, acknowledging A through 30 leaves both B messages unread. Unread counts compare messages against their own conversation's read position; they do not use one global read position.

A read marker acknowledges a local prefix, not an exact application-processing ACK.

## Refresh after changes

`ConversationChanged` reports updates to the local conversation projection. Use `GetConversationAsync` to retrieve a specific conversation or open a new filtered reader to rebuild the visible list. Each reader holds a fixed SQLite snapshot, so an already open reader will not grow when another message arrives or when a read marker changes.

Use the returned `ConversationId` as the API identifier instead of constructing one from a title or assuming that every conversation is a direct-message account ID. The `Kind` identifies the resource category; use the appropriate message, channel, or group APIs when opening that conversation's content.

Queries can fail because of an unmigrated, inaccessible, or incorrectly bound database. Handle those failures visibly instead of displaying an empty conversation list as though loading succeeded. Dispose readers even when a user cancels or navigates away.

## API reference

[MeshlineClient](../api/Meshline.MeshlineClient.md) · [Conversation](../api/Meshline.Models.Client.Conversation.md) · [ConversationQuery](../api/Meshline.Models.Client.ConversationQuery.md) · [ConversationChangedEventArgs](../api/Meshline.Components.ConversationChangedEventArgs.md)
