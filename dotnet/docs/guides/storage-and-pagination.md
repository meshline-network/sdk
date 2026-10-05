# Storage and pagination

[Guide index](../README.md) · [Conversations](conversations.md)

## Preserve the local session

SQLite is the supported database engine, configured internally through EF Core. `DatabaseOptions.Path` resolves relative paths against the current working directory when assigned, without opening a connection. No provider registration or `DbContextOptions` is required.

SQLite stores device state, selected protected secrets, contacts, messages, pending operations, synchronization positions, and local conversation read positions. Create the database directory and apply `MeshlineDatabase.MigrateAsync` before initialization. The database is bound to the configured account and network; do not reuse it for another account or network.

Keep a distinct database for each local account/device session. Preserve its protection keys as well as the database. Restoring a file without the ability to decrypt its secret values does not restore a usable device. Selected secret protection does not encrypt the full database. Backups and access controls belong to the application; coordinate backups with SQLite rather than copying an actively changing database file blindly.

Schema migrations are applied explicitly. Use the SDK migration entry point instead of editing internal tables or invoking EF migration classes directly. The `InitialCreate` migration is the 1.0.0 database baseline; pre-1.0 development databases built from a different initial migration are outside that upgrade baseline.

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

## Read a bounded history range

Use `HistoryRange` to read newer messages with `After` or older messages with
`Before`. Resource filters, the range, and the cancellation token are optional.
An omitted, null, or empty range is unbounded. `HistoryRange` validates and copies
its bounds when the manager opens a query. `ReadNextAsync(count)` chooses the
batch size. Every batch is ascending; the boundary selects the adjacent batch.

| API | Sequence scope and filters |
| --- | --- |
| `MessageManager.GetMessageHistoryAsync(peerAccountId, range, cancellationToken)` | This database's account-wide `LocalSequence`; optional peer filter. |
| `GroupManager.GetMessagesAsync(groupId, sender, range, cancellationToken)` | Group event `Sequence` (also exposed as `LocalSequence`); optional sender filter. |
| `ChannelManager.GetPostsAsync(channelId, author, range, cancellationToken)` | Channel original publication sequence (`LocalSequence`); optional author filter. |

`After` and `Before` are **exclusive**, nonnegative safe-integer bounds. If both are supplied, `After` must be smaller than `Before`. Group and channel queries require `groupId` or `channelId` when using bounds, because their sequences are scoped to one resource. Sequence gaps are valid. Only direct messages, readable group messages, and undeleted channel posts count toward the batch size.

For stored sequences 1 through 100 and a batch size of 10:

| Bounds | First batch | Next batch on the same reader |
| --- | --- | --- |
| `After = 80` | 81–90 | 91–100 |
| `Before = 80` | 70–79 | 60–69 |
| `After = 65, Before = 80` | 70–79 | 66–69 |
| None | 1–10 | 11–20 |

With `Before`, the database selects the closest earlier items in descending order, and the reader returns each selected batch in ascending order. Earlier batches belong before previously displayed batches; simply appending all backward batches will not produce one globally ascending list. Without `Before`, the reader moves forward. Unbounded group and channel queries use resource-ID/sequence order.

<!-- snippet: history-before -->
```csharp
public static async Task<IReadOnlyList<MessageInfo>> ReadPreviousMessagesAsync(
    MeshlineClient client, string peerAccountId, long before,
    CancellationToken cancellationToken = default)
{
    await using var reader = await client.MessageManager.GetMessageHistoryAsync(
        peerAccountId, new HistoryRange { Before = before }, cancellationToken);
    return await reader.ReadNextAsync(50, cancellationToken);
}
```
<!-- /snippet -->

Source: [Messaging.cs](../../examples/Meshline.Sdk.Examples/Messaging.cs). This returns up to the 50 messages immediately before the supplied position, in ascending order, and disposes the reader.

### Resume with a new reader

To reopen and read an older batch, use the **first** returned message's `LocalSequence` as `Before`. To resume toward newer messages, use the **last** returned position as `After`. Preserve the database, resource, other filters, and opposite bound when resuming; stop at an empty batch. If the updated bounds meet, the bounded range is exhausted.

The database applies bounds and ordering before reading each batch; it does not materialize the whole history to select its tail. Repeated `ReadNextAsync` calls share the reader's fixed snapshot. Opening another reader, including after a restart, captures current local state. Later group decryption can reveal messages at older positions, and channel edits or deletions can change older results, so refresh those ranges when needed. A position is not an exact processing acknowledgement or proof of complete history. These queries do not synchronize, follow, or mark read. A canceled read does not advance the reader.

### Calling forms and compatibility

Use `GetMessageHistoryAsync()` for all locally stored direct history, or
`GetMessageHistoryAsync(range: new HistoryRange { After = 80 })` for newer items.
For groups, `GetMessagesAsync(groupId: groupId, range: new HistoryRange { Before = 80 })`
omits the sender and cancellation token. The same optional-argument pattern applies
to channels. Existing full-argument signatures remain available for binary
compatibility, including calls such as `GetMessageHistoryAsync(peerAccountId, default)`.

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

[MeshlineDatabase](../api/Meshline.Storage.MeshlineDatabase.md) · [DatabaseOptions](../api/Meshline.Storage.DatabaseOptions.md) · [QueryReader](../api/Meshline.Storage.QueryReader_T_.md) · [HistoryRange](../api/Meshline.Models.Client.HistoryRange.md) · [Page](../api/Meshline.Models.Client.Page_T_.md) · [PageRequest](../api/Meshline.Models.Client.PageRequest.md)
