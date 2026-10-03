# Meshline SQLite storage for Node.js

`NodeSqliteStore` provides persistent transactional storage for a Meshline
application on **Node.js 24 or later**. The package is ESM with TypeScript declarations.

## Install and configure

Install this adapter and the matching core together:

```sh
npm install @meshline/sdk@0.1.0-alpha.1 @meshline/storage-node@0.1.0-alpha.1
```

Import `NodeSqliteStore` from `@meshline/storage-node` and construct it
with a database path. Create the parent directory yourself. Construction does no
I/O; call `migrate()`, then pass the store to `MeshlineClient` and initialize the
client. Direct store consumers call `initialize({ context, accountId })` instead.

The [Node session example](https://github.com/meshline-network/sdk/blob/main/typescript/examples/node.ts)
also configures `@meshline/transport-node` and handles cleanup.

## Persistence

One database stores one network/account/local device. Retain it with the same
persistent secret protector across restarts. WAL and full synchronization are
enabled. Transactions atomically compare revisions and commit records plus cursors.
Conflicts surface to the caller.

Query readers hold their own read-only transaction and must be disposed.
Dispose the store after the client, relay pool, and readers have stopped.
Database contents are not encrypted; the SDK's secret protector covers selected
secrets, not message text or the entire file. This database format is not
interchangeable with the .NET SDK's SQLite format.

Database calls use synchronous `node:sqlite`. Keep large workloads in an
application-owned worker when event-loop latency matters.

[Storage guide](https://github.com/meshline-network/sdk/blob/main/typescript/docs/guides/storage-and-pagination.md)
· [Platform limits](https://github.com/meshline-network/sdk/blob/main/typescript/docs/platforms.md)
