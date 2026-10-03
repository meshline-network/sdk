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
| Direct history | Creation time, message ID, sender ID |
| Group history | Creation time, group ID, sequence |
| Channel posts | Channel ID, sequence |
| Conversations | Latest summary timestamp descending, conversation ID; empty last |

Optional filters apply when supplied. An omitted filter and an explicitly empty
array can have different meanings. Identifiers are validated even in local
queries; an empty relay ID is not a request for all relays.

## Distinguish relay pages

Local readers do not fetch missing remote history.
`loadChannelHistory` and group invitation/application/recovery listings instead
return pages containing `items` and an optional `nextCursor`. Keep a relay cursor
with the original resource and query. Stop when no next cursor is returned.
These page objects are not disposable readers.

## Preserve durable state

The store commits state and cursors atomically. A failed local write or secret
protection operation must not be treated as successfully consumed history.
Allow the SDK to retry from retained state, and report persistent failures.

If implementing your own `MeshlineStore`, preserve atomic compare-and-commit,
binding validation, fixed snapshots, cancellation, and disposal semantics.
Do computation, signing, and network I/O outside database transactions.
A stale revision requires recomputing from a new read, not replaying an external
business request blindly.

[All guides](../README.md)
