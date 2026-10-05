# Storage and pagination

Use one persistent store per network, account, and local device. A second device
needs a separate database. Do not copy a live database between device identities
or substitute an empty database when opening the original fails.

## Choose a store

| Runtime | Store | Application considerations |
| --- | --- | --- |
| Node.js | `NodeSqliteStore(path)` | Create the parent directory; synchronous SQLite calls can affect event-loop latency. |
| Browser | `IndexedDbStore(databaseName)` | Requires a secure origin and Web Locks; quotas, eviction, and user clearing apply. |
| Expo | `ExpoSqliteStore({ databaseName })` | Requires native SQLite and a native application build. |

Construction performs no I/O. Run `migrate()` explicitly, then initialize the
client, which supplies the store's network/account binding. If using a store
directly, call `initialize({ context: context.toString(), accountId })`.
The adapters do not fall back to in-memory storage on persistence failure.

SDK secret protection covers selected key material. It does not encrypt the
entire database, message bodies, or metadata. Keep the original persistent
protector available when reopening.

The database format belongs to each adapter. The TypeScript SQLite format is
not compatible with the .NET SDK's database; neither should be opened as the other.

## Read local snapshots

Query methods such as `getMessageHistory`, `getConversations`, `getPosts`,
and `getMembers` return `QueryReader<T>`. Read positive page sizes with
`readNext(count, signal?)`; an empty batch means the snapshot is exhausted.
Always dispose a reader in `finally`.

The `consumePages` example in [workflows.ts](../../examples/workflows.ts) consumes
all pages and releases the reader even if rendering or cancellation fails.
`unreadConversations` shows reading only the first page.

A reader is fixed when opened. Concurrent arrivals do not appear in it, and a
canceled read does not advance it. Open a new reader to refresh. Do not keep
readers open for the lifetime of a screen that only needs materialized rows:
SQLite readers retain read transactions and browser readers retain snapshot rows.

| Local query | Ordering |
| --- | --- |
| Direct history | Local sequence ascending |
| Group history | Group ID, sequence ascending |
| Channel posts | Channel ID, sequence |
| Conversations | Latest summary timestamp descending, conversation ID; empty last |

Optional filters apply when supplied. An omitted filter and an explicitly empty
array can have different meanings. Identifiers are validated even in local
queries; an empty relay ID is not a request for all relays.

## Read a bounded history range

Use the exported `HistoryRange` type to read newer messages with `after` or older
messages with `before`. Resource filters, the range, and the signal are optional.
Omitted, null, and empty ranges are unbounded. Managers copy and validate bounds
when opening the query. `readNext(count, signal?)` chooses the batch size. Every
batch is ascending; the boundary selects the adjacent batch.

| Range overload | Resource filters | Sequence |
| --- | --- | --- |
| `messageManager.getMessageHistory(peer?, range?, signal?)` | Optional peer string; pass `undefined` for all peers. | Account-wide `localSequence` in this database. |
| `groupManager.getMessages(filter?, range?, signal?)` | `groupId`, `sender` | Group event `sequence`, also exposed as `localSequence`. |
| `channelManager.getPosts(query?, range?, signal?)` | `channelId`, `author` | Original publication `localSequence` in the channel. |

Both bounds are **exclusive**, nonnegative safe integers. When supplied together,
`after` must be smaller than `before`. Group/channel bounds require one
`groupId`/`channelId`, because sequences belong to that resource. Sequence gaps
are valid. Only direct messages, readable group messages, and undeleted posts
count toward a batch's size.

For stored sequences 1 through 100 and a batch size of 10:

| Bounds | First batch | Next batch on the same reader |
| --- | --- | --- |
| `after: 80` | 81–90 | 91–100 |
| `before: 80` | 70–79 | 60–69 |
| `after: 65, before: 80` | 70–79 | 66–69 |
| None | 1–10 | 11–20 |

With `before`, the store selects the closest earlier items in descending order,
and the reader returns each selected batch in ascending order. Prepend earlier
batches to an existing list; appending backward batches does not produce globally
ascending history. Without `before`, the reader moves forward. Unbounded group and channel
queries use resource-ID/sequence order.

```ts
export async function previousMessages(client: MeshlineClient, peerAccountId: string, before: number, signal?: AbortSignal) {
    const reader = await client.messageManager.getMessageHistory(peerAccountId, { before }, signal);
    try { return await reader.readNext(50, signal); } finally { await reader.dispose(); }
}
```

The example is compiled in [workflows.ts](../../examples/workflows.ts). It reads
up to 50 messages immediately before the supplied position, in ascending order.

### Resume with a new reader

To reopen and read older messages, use the **first** returned `localSequence`
as `before`. To resume toward newer messages, use the **last** returned position
as `after`. Keep the database, resource, other filters, and opposite bound.
Stop at an empty batch; if updated bounds meet, the bounded range is exhausted.

Each reader retains one fixed snapshot, even across `readNext` calls. A newly
opened reader captures current local state. Later synchronization, group
decryption, channel edits and deletions can change results; refresh older ranges
to discover messages that become readable there. These positions are not exact
processing ACKs or proof of complete history. Local queries do not synchronize,
mark read, or follow. A canceled read does not advance the reader.

### Calling forms and compatibility

Existing calls such as `getMessageHistory(peer, signal)` and
`getMessages({ groupId }, signal)` continue to use the original overload.
Use `getMessageHistory()` for unbounded history,
`getMessageHistory(undefined, { after: 80 })` for all peers after a position,
or `getMessageHistory(peer, { before: 80 })` for earlier messages with one peer.
`getMessageHistory(peer, null, signal)` explicitly requests an unbounded query.

## Distinguish relay pages

Local readers do not fetch missing remote history.
`loadChannelHistory` and group invitation/application/recovery listings instead
return pages containing `items` and an optional `nextCursor`. Keep a relay cursor
with the original resource and query. Stop when no next cursor is returned.
These page objects are not disposable readers.

## Storage adapter behavior

The supplied adapters apply key bounds and ordering in storage. Direct history
and queries scoped to one group/channel use storage readers: SQLite materializes
bounded batches, while IndexedDB copies the selected range to snapshot storage
when opening a reader. Unscoped group/channel queries still materialize their
matching local history when opened. Group/channel filtering may inspect multiple
batches to skip non-message, deleted, or nonmatching rows.

Private-message history uses account-wide and per-peer sequence indexes containing
message references. New messages maintain these indexes in their transaction.
The first history query of an older store backfills them in atomic batches of at
most 256 source rows, retaining a restartable checkpoint on failure or cancellation.
That initial query can take longer than later indexed queries.

## Preserve durable state

The store commits state and cursors atomically. A failed local write or secret
protection operation must not be treated as successfully consumed history.
Allow the SDK to retry from retained state, and report persistent failures.

### Custom store contract

If implementing your own `MeshlineStore`, preserve atomic compare-and-commit,
binding validation, fixed snapshots, cancellation, and disposal semantics.
Support `RecordQuery.after` and `before` as exclusive ASCII key bounds intersected
with `key`/`prefix`, `reverse` as key ordering, and `limit` as a positive maximum
row count. The limit applies to the whole `openQuery` snapshot, not separately to
each `readNext` call. Empty intersections return no rows. Ignoring these fields
can invalidate history pagination; use updated SDK and storage adapter packages
together. Adapters supplied here require no schema change for these key queries.
Do computation, signing, and network I/O outside database transactions.
A stale revision requires recomputing from a new read, not replaying an external
business request blindly.

[All guides](../README.md)
