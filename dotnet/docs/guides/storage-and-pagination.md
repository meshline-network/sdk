# Storage and pagination

[Guide index](../README.md) · [Conversations](conversations.md)

## Preserve the local session

SQLite stores device state, selected protected secrets, contacts, messages, pending operations, synchronization positions, and local conversation read positions. Create the database directory and apply `MeshlineDatabase.MigrateAsync` before initialization. The database is bound to the configured account and network; do not reuse it for another account or network.

Keep a distinct database for each local account/device session. Preserve its protection keys as well as the database. Restoring a file without the ability to decrypt its secret values does not restore a usable device. Selected secret protection does not encrypt the full database. Backups and access controls belong to the application; coordinate backups with SQLite rather than copying an actively changing database file blindly.

Schema migrations are applied explicitly. Use the SDK migration entry point instead of editing internal tables or invoking EF migration classes directly. Preserve the published database baseline when upgrading the SDK.

## Local snapshot readers

List APIs such as `GetMessageHistoryAsync`, `GetConversationsAsync`, `GetContactsAsync`, `GetPostsAsync`, and `GetMessagesAsync` return `QueryReader<T>`. Each reader opens a fixed SQLite snapshot:

<!-- snippet: history -->
```csharp
public static async Task PrintLocalHistoryAsync(
    MeshlineClient client, string peerAccountId, CancellationToken cancellationToken = default)
{
    await using var reader = await client.MessageManager.GetMessageHistoryAsync(
        peerAccountId, cancellationToken);
    while (true)
    {
        var batch = await reader.ReadNextAsync(50, cancellationToken);
        if (batch.Count == 0) break;
        foreach (var message in batch)
            Console.WriteLine($"{message.Key.Sender}: {message.Body?.Text}");
    }
}
```
<!-- /snippet -->

Source: [Messaging.cs](../../examples/Meshline.Sdk.Examples/Messaging.cs). Pass a positive batch size. An empty batch marks completion. Dispose the reader on completion, cancellation, or navigation. A newly arrived message does not extend the existing snapshot; open another reader to refresh. Leaving readers open retains their transactions and database connections.

## Relay-backed cursors

Relay history and administration APIs return `Page<T>` with `Items` and `NextCursor`, and accept a `PageRequest`. A positive `Limit` requests a maximum page size; an omitted limit uses the operation's default. The relay's own limits still apply.

Pass `NextCursor` unchanged to the same operation and resource, and stop when it is `null`. Do not derive cursors from item counts or mix them with local-reader offsets. [Channel history](channels.md#read-history-and-local-posts) provides a compiled cursor example; group invitation, application, and key-recovery lists use the same page abstraction.

| Local reader | Relay page |
| --- | --- |
| Reads a fixed local snapshot. | Makes a network request against relay-visible state. |
| Continue with `ReadNextAsync`. | Continue with `PageRequest.Cursor`. |
| Empty batch ends the reader. | `NextCursor == null` ends paging. |
| Reader must be disposed. | No reader lifetime; each call has cancellation and network failure semantics. |

## API reference

[MeshlineDatabase](../api/Meshline.Storage.MeshlineDatabase.md) · [DatabaseOptions](../api/Meshline.Storage.DatabaseOptions.md) · [QueryReader](../api/Meshline.Storage.QueryReader_T_.md) · [Page](../api/Meshline.Models.Client.Page_T_.md) · [PageRequest](../api/Meshline.Models.Client.PageRequest.md)
