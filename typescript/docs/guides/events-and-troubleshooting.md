# Events and troubleshooting

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
| Transport/deadline/cancellation failure | Preserve pending state; interruption is not proof of remote rejection. |
| Local storage/protection failure | Restore access to the original store/protector and retry without discarding state. |

## Troubleshooting

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| Client cannot open its database | Missing migration, incorrect binding, inaccessible storage, or lost protector | Migrate explicitly; check the network/account/database and restore access to the original protection keys. Surface corruption instead of opening a new empty store. |
| Existing account fails initial establishment | This database has no authorized device for the existing route | Use the application's explicit account recovery flow with an account signer. |
| Send returns but the peer has no message yet | The return is an outbox status, or background work is stopped | Start the client, inspect `getSendStatus`, and observe `sendStatusChanged`. |
| Page does not show a new arrival | The reader holds a fixed snapshot | Dispose it and open a new reader. |
| Profile edit or migration reports a conflict | An earlier request is uncertain or authoritative state changed | Let the original operation reconcile; inspect the current state before choosing another edit or target. |
| New device cannot decrypt old group messages | Historical secrets are unavailable on that device | Keep another authorized device running for key synchronization; recovery alone cannot recreate missing historical keys. |
| Group approval does not complete a pending request | Approval does not verify the original candidate, or history/key processing failed | Keep the original store/protector, inspect background errors, and verify the correct application's approval. |
| Expo cannot find `MeshlineRelaySocket` | Expo Go or an old native binary lacks the module | Rebuild the native app after installing the adapter. |
| Android reports a released SQLite object or incomplete HTTP body | Pinned Expo native dependencies may be missing compatibility fixes | Apply and check the [Android fixes](../platforms.md#android-compatibility), then rebuild. If already applied, retain the error for diagnosis. |
| Browser relay traffic carries cookies | Ambient WebSocket policy or the known Windows WebKit fetch issue | Use a cookie-free relay origin and review [browser limits](../platforms.md#browser). |
| Authentication stays rejected after a public request succeeds | A public request does not reauthorize the device | Check route/device authorization and establish a newly authenticated session. |

[All guides](../README.md)
