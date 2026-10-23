# Lifecycle and ownership

Construction performs no I/O. Open a session in this order:

1. Create the platform store and relay pool.
2. Call `store.migrate()` explicitly.
3. Construct `MeshlineClient` and call `client.initialize()`.
4. Explicitly call `establishAccount()` if authorization is needed; valid retained devices can start directly.
5. Call `client.start()` to activate background work.

The [platform helpers](../../examples/README.md) perform the first three steps
and clean up if setup fails. Client initialization initializes its components
and storage binding; it does not create a missing database schema.

## Resource ownership

| Resource | Owner | Cleanup |
| --- | --- | --- |
| Six managers | `MeshlineClient` | The client starts, stops, and disposes them. |
| Relay pool and store | Application | Dispose after the client. |
| Signer, registry, protector | Application | Retain for the session; release their resources as required by the integration. |
| Query reader | Application that opens it | Dispose in `finally`. |
| Work started by an event handler | Application | Track and await it separately if it must finish. |

The six managers are `accountManager`, `deviceManager`, `profileManager`,
`messageManager`, `channelManager`, and `groupManager`. Their foreground APIs
remain available after initialization while the client is stopped, subject to
each operation's authorization requirements. A stopped client does not process
its background outbox or synchronization loops.

## Stop, resume, and dispose

Call `await client.stop()` when suspending long-running SDK work. Resume the same
initialized instance with `await client.start()`. After process restart, reopen
the original store with the original protector, initialize a new client, and start it.
Do not automatically call account recovery on every launch.

Startup reuses retained valid authorization and connects in the background. It
does not resume unfinished establishment or request account signatures. A removed
device or a definitive device authentication rejection stops synchronization and
drains device connections. Use explicit setup to reauthorize. Network failures
leave cached state intact and do not establish revocation.

Keep one active client graph per database. Do not overlap explicit setup with
standalone device mutations. See [account establishment](account-establishment.md).

`dispose()` ends the client lifetime and disposes its managers. It retains
caller-owned dependencies. Dispose the client before the relay pool and store;
[ownedSession](../../examples/shared.ts) attempts cleanup of every resource and
preserves cleanup failures in an `AggregateError`.

`lifecycleState` is `uninitialized`, `stopped`, `running`, `stopping`, or `disposed`.
Use `onLifecycle('stateChanged', listener)` for transitions and subscribe to
background errors on each emitting component. See [events](events-and-errors.md).

Mobile suspension can stop JavaScript or terminate the process without allowing
cleanup. Durable state supports later recovery; it does not guarantee execution
while suspended. See [platform limits](../platforms.md).

For a foreground refresh or background progress, see [synchronization](synchronization.md). Starting the client does not wait for resources to catch up.

[All guides](../README.md)
