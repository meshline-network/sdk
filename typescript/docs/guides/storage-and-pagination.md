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

## Account setup persistence

One `MeshlineClient` component graph owns a database at a time. Retain the complete
installation and its protection material together. The
[account establishment contract](account-establishment.md#local-data-and-ownership)
defines the ownership and concurrency requirements.

| Records | Owner | Durable responsibility |
| --- | --- | --- |
| `client_account_operations` | Client | Original setup parameters, internal operation ID and next workflow step; also explicit migration progress. |
| `device_preparation` | Device manager | Protected draft keys and certificate before authorization; an activation receipt after those keys become the local device. |
| `local_device`, `identity_binding/device` | Device manager | Current certificate, protected keys and device binding. |
| `signed_requests` | Publishing manager | Account, device or profile intent, signed request, revision and retry state. Device requests also associate establishment with its explicit replacement choice. |
| `contacts`, `contact_requests`, `message_outbox` | Message manager | Issued grants, target-acceptance evidence and outgoing messages used to resume authorization maintenance. |

The client checkpoints a step after its manager confirms completion. On retry,
each manager reconciles its own effects before repeating work. For example, the
device manager determines whether a saved publication matches the intended
replacement and whether authorization is effective. Internal operation IDs are
stored by the SDK; applications do not supply them.

Setup uses atomic compare-and-commit storage; wallet signing and network I/O
happen outside write transactions. A settled signed request may have been rejected:
successful setup also requires verified, effective authorization.

Contact maintenance resumes from persisted grants, outgoing messages and
per-contact notification progress. Progress and its outbox request commit together
and survive stops and restarts.

Reauthorizing a device does not restore messages or decryption keys from a lost
installation.

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
Omitted, null, and empty ranges are unbounded. Without a range, pass the signal
directly after the resource filter, as in `getMessageHistory(peer, signal)`.
Managers copy and validate bounds when opening the query. `readNext(count, signal?)`
chooses the batch size. Every batch is ascending; the boundary selects the adjacent batch.

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

## Distinguish relay pages

Local readers do not fetch missing remote history.
`loadChannelHistory` and group invitation/application/recovery listings instead
return pages containing `items` and an optional `nextCursor`. Keep a relay cursor
with the original resource and query. Stop when no next cursor is returned.
These page objects are not disposable readers.

## Storage adapter behavior

SQLite readers hold read transactions and materialize bounded batches. IndexedDB
copies the selected range, or all declared collections for `openSnapshot`, into
owned snapshot storage. Opening time and temporary database space scale with that
selection. Unscoped group/channel queries materialize their matching history;
filtered queries may inspect several batches before returning readable messages.

Private-message history uses sequence indexes. An older store's first history
query may take longer while the SDK builds those indexes; interrupted
indexing resumes on the next attempt.

### Custom store contract

Implement `MeshlineStore` with binding validation, atomic compare-and-commit,
fixed snapshots, cancellation and resource disposal. Use matching SDK and adapter
versions.

| Read method | Snapshot and paging contract |
| --- | --- |
| `read(queries, signal?)` | Returns the requested records from one current version. |
| `openQuery(query, signal?)` | Fixes one selection at open time and pages it with `readNext`. An empty page marks completion. |
| `openSnapshot(collections, signal?)` | Fixes all declared collections at one version before returning. Subsequent `read` calls share that version despite concurrent writes; queries outside those collections must fail. |

Use `openSnapshot` to read related collections in bounded pages at one version.
Disposing a reader, snapshot or its store releases the associated resources;
already returned values remain usable.

`RecordQuery.after` and `before` are exclusive ASCII key bounds intersected with
`key`/`prefix`; `reverse` selects descending key order. `limit` is a positive maximum
row count, applied to the whole `openQuery` selection rather than each `readNext`
call. Empty intersections return no rows. Canceling `readNext` must not advance it.

Commit state and synchronization cursors atomically. A stale revision requires
recomputing from a new read; never replay an external business request blindly.
Keep computation, signing and network I/O outside write transactions. Failed writes
or secret-protection operations must leave work available for retry.

[All guides](../README.md)
