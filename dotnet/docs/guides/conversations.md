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

// Call only when the application considers the conversation read.
public static Task MarkConversationReadAsync(
    MeshlineClient client, string conversationId, CancellationToken cancellationToken = default) =>
    client.MarkReadAsync(conversationId, cancellationToken);
```
<!-- /snippet -->

Source: [Messaging.cs](../../examples/Meshline.Sdk.Examples/Messaging.cs). The list method only displays unread counts. Call the separate read-position method after the user has actually viewed the conversation according to your application's UX. `MarkReadAsync` updates a local read position; it does not send a recipient read receipt to another account.

## Refresh after changes

`ConversationChanged` reports updates to the local conversation projection. Use `GetConversationAsync` to retrieve a specific conversation or open a new filtered reader to rebuild the visible list. Each reader holds a fixed SQLite snapshot, so an already open reader will not grow when another message arrives or when a read marker changes.

Use the returned `ConversationId` as the API identifier instead of constructing one from a title or assuming that every conversation is a direct-message account ID. The `Kind` identifies the resource category; use the appropriate message, channel, or group APIs when opening that conversation's content.

Queries can fail because of an unmigrated, inaccessible, or incorrectly bound database. Handle those failures visibly instead of displaying an empty conversation list as though loading succeeded. Dispose readers even when a user cancels or navigates away.

## API reference

[MeshlineClient](../api/Meshline.MeshlineClient.md) · [Conversation](../api/Meshline.Models.Client.Conversation.md) · [ConversationQuery](../api/Meshline.Models.Client.ConversationQuery.md) · [ConversationChangedEventArgs](../api/Meshline.Components.ConversationChangedEventArgs.md)
