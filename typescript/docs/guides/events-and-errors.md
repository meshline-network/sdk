# Events and errors

Use `on(name, listener)` for domain events and
`onLifecycle(name, listener)` for component state and background failures.
Both return an unsubscribe function.

The `observeClient` function in [workflows.ts](../../examples/workflows.ts)
subscribes to conversation changes and to background errors on the client and all
six managers. Failures belong to the emitting component; observing only the
client does not replace subscriptions to its managers.

## Event behavior

Listeners run in registration order without awaiting returned promises. A pending
listener does not defer later listeners. Keep synchronous work short; handlers may
stop or dispose their component or owning client.

Observer failures are reported through that component's `backgroundError` and
retained in `lastBackgroundError`. They do not undo a committed operation or
change its foreground result. If an error observer also fails, diagnostics retain
both errors. SDK shutdown does not drain asynchronous application callback work;
track any work that must finish separately.

| Emitter | Event | Payload |
| --- | --- | --- |
| Client | `conversationChanged` | Conversation ID and `created`, `updated`, or `removed` kind |
| Account manager | `accountChanged` | Current account state and route snapshot |
| Device manager | `deviceChanged`, `deviceStateChanged` | Device ID or accepted device-state snapshot |
| Profile manager | `profileChanged` | Profile snapshot or `undefined` |
| Message manager | `messageReceived`, `sendStatusChanged`, `timelineChanged` | Message array, send status, or no payload |
| Message manager | `contactChanged`, `contactRequestChanged` | Identity, committed optional snapshot, and change classification |
| Channel manager | `channelChanged`, `timelineChanged`, `followChanged` | Channel snapshot, channel/post changes, or follow status |
| Group manager | `groupChanged`, `timelineChanged` | Group snapshot with change kinds, or group/sequence with optional decrypted message |
| Group manager | `applicationsChanged`, `keyRecoveryChanged` | Group reference |
| Message/group/channel managers | `syncStatusChanged` | Local resource progress; see [synchronization](synchronization.md#observe-local-progress). |
| Any client component | `stateChanged`, `backgroundError` via `onLifecycle` | Previous/current state, or operation/resource/error |

A group timeline event may have no `message`; check it before appending chat
content. Notifications about verified history need not contain decrypted content.

`contactChanged.kinds` contains `relationship`, `alias`, `authorization`, or
`deleted`; `contact` is absent on deletion. Authorization reflects device state
cached in the committed transaction. `contactRequestChanged` includes
`direction`, an `added`/`updated`/`removed` kind, and `request` when it still exists.

`groupChanged` preserves `ref`, `group`, `membership`, and `role`, and adds
`kinds`: `properties`, `members`, `roles`, `bans`, `nickname`, or `status`.
Verified management changes produce a combined snapshot per committed history
page, even if a later page fails. Nickname and local membership changes have
their own committed snapshots. Failed commits do not publish partial changes.

## Cancellation and error types

Operations accept optional `AbortSignal`; some options objects carry `signal`.
Cancellation preserves the signal's reason when the runtime provides it.
On legacy React Native controllers that discarded a caller's reason, the SDK
returns `AbortError`; it cannot reconstruct that value and does not replace the
global controller. SDK-owned scopes preserve their cancellation reasons.

| Error | How to handle it |
| --- | --- |
| `ProtocolError` with a code | Inspect invalid input, identity, authorization, or protocol state. |
| `TypeError` / `RangeError` | Correct argument shape or range. |
| `StateConflictError` | Refresh/reconcile state before choosing the next operation. |
| `RelayError` | Inspect retained relay error metadata; distinguish permissions from authentication. |
| `TimeoutError` | Inspect `operation`, `timeoutMilliseconds`, and the original `cause`; reconcile writes before retrying. |
| Caller abort / `AbortError` | Cancellation does not establish remote rejection or an SDK deadline. |
| Other transport failure | Preserve the original error and pending state. |
| Local storage/protection failure | Restore access to the original store/protector and retry without discarding state. |

SDK-owned deadlines cover Registry RPC, Relay HTTP, WebSocket connection/readiness,
and RPC waits. HTTP deadlines include response body reads. If an HTTP adapter
returns a generic `AbortError` after the SDK deadline, the SDK restores the
`TimeoutError` reason and retains the adapter error as `cause`.

Caller aborts preserve the signal's reason where the runtime supports it; SDK
disposal uses `AbortError`. An unrelated dependency abort is not converted to a
timeout. Application adapters can impose their own deadlines. A lost response,
timeout, or abort does not prove that a mutation failed remotely: reconcile the
persisted operation before retrying. [Aborting a send wait](direct-messages.md#wait-for-an-acceptance-milestone)
leaves the outgoing message active.

See [transport configuration](transport.md#request-timeouts-and-cancellation) and
[symptom-based troubleshooting](troubleshooting.md).

[All guides](../README.md)
