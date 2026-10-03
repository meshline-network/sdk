# Meshline IndexedDB storage

`IndexedDbStore` provides persistent transactional storage in a browser.
It requires **IndexedDB, Web Locks, and cryptographic randomness on a secure origin**.
The package is ESM with TypeScript declarations.

## Install and configure

Install this adapter and the matching core together:

```sh
npm install @meshline/sdk@alpha @meshline/storage-browser@alpha
```

Import `IndexedDbStore` from `@meshline/storage-browser` and construct it
with a database name. Construction does no I/O. Call `migrate()`, pass it to
`MeshlineClient`, then initialize the client. Direct store consumers call
`initialize({ context, accountId })` instead.

Use one database for each network/account/local device. The
[browser example](https://github.com/meshline-network/sdk/blob/main/typescript/examples/browser.ts)
shows the complete session and cleanup.

## Persistence

Commits atomically compare the database revision and write state plus cursors.
Writes request strict durability and resolve after transaction completion.
Query readers materialize independent snapshot rows, then release the IndexedDB
transaction before returning. Dispose readers to remove their rows.
Web Locks distinguish live readers from abandoned snapshots after a crash.

Quotas, eviction, user clearing, and private-mode restrictions still apply.
Errors surface to the application; there is no in-memory fallback. Applications
may request persistent browser storage. Preserve access to the original secret
protector across reloads. The adapter does not encrypt the database or message text.

This package supplies storage. Browser HTTP and WebSocket cookie behavior has
separate constraints; review them before choosing a relay origin.

[Storage guide](https://github.com/meshline-network/sdk/blob/main/typescript/docs/guides/storage-and-pagination.md)
· [Browser requirements and limits](https://github.com/meshline-network/sdk/blob/main/typescript/docs/platforms.md#browser)
