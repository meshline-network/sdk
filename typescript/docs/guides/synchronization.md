# Synchronization

[Guide index](../README.md) · [Lifecycle](lifecycle.md) · [Storage and pagination](storage-and-pagination.md)

## Choose how to refresh

| Application task | API or behavior |
| --- | --- |
| Read what is already stored | Local query readers; they do not synchronize or mark read. |
| Keep an active session updated | `start()` runs background synchronization, subscriptions, and polling. |
| Await a fresh account timeline pass | `messageManager.synchronize(relayId, signal?)` |
| Await a fresh group pass | `groupManager.synchronize(groupRef, signal?)` |
| Await a fresh channel pass | `channelManager.synchronize(channelRef, signal?)` |
| Inspect progress without network I/O | The manager's `getSyncStatus(resourceId)` and `syncStatusChanged` event. |

HTTP reads transfer synchronized data. WebSocket notifications trigger earlier reads;
periodic polling continues when notifications are unavailable. Newly confirmed or
restored subscriptions trigger an HTTP catch-up read. Startup does not wait for
WebSocket readiness. Observe each component's [background diagnostics](events-and-errors.md).

## Run a foreground pass

For a foreground refresh, call `messageManager.synchronize(relayId, signal)`, `groupManager.synchronize(groupRef, signal)`, or `channelManager.synchronize(channelRef, signal)`. Initialize the component and its dependencies and establish usable device authorization first; `start()` is not required. Each call performs a fresh pass, serialized with background synchronization, even if the cached status already says `caughtUp`. Its return value is the snapshot for that pass.

```ts
export async function refreshGroup(client: MeshlineClient, relayId: string, group: GroupRef, signal?: AbortSignal) {
    await client.messageManager.synchronize(relayId, signal);
    return client.groupManager.synchronize(group, signal);
}
```

Source: [workflows.ts](../../examples/workflows.ts). The example refreshes account recovery messages before the group. Inspect the returned `state`; only `caughtUp` confirms completion of this pass. Then open a [local history reader](storage-and-pagination.md) to display stored messages. Supply a caller-owned `AbortController` and timer when a deadline is needed, and clear the timer when done.

## Completion and cancellation

The pass reads forward from stored progress until the relay reports no more pages and completes required local processing. Groups also synchronize keys and decrypt readable messages; missing keys return `blocked` without waiting indefinitely for recovery.

Operational failures update the resource status and reject the call.

Cancellation preserves committed pages so another call can resume. A queued canceled call never starts a pass.

Stopping background work does not cancel independent foreground calls; use their signals or dispose the component.

Manual group synchronization first consumes pending group private-state and history-secret messages already stored in the account timeline. Call `client.messageManager.synchronize(relayId, signal)` for the relevant relay first to download newly arrived account recovery messages.

The pass does not freeze the remote head at invocation or guarantee complete history.

Continuous arrivals can prolong it, so provide a cancellation deadline.

Account synchronization targets one relay; channel synchronization does not follow the channel, and group synchronization does not join a group or grant historical access. One-shot calls do not start background workers or notification subscriptions.

## Observe local progress

The message, group, and channel managers expose `getSyncStatus(resourceId)` and `on('syncStatusChanged', listener)`. Queries return local snapshots without network requests. Subscribe before querying to observe subsequent changes; event handlers must not synchronously wait for component operations. Component startup does not itself imply that resources have caught up.

| Manager | Resource identifier |
| --- | --- |
| MessageManager | Relay ID: all contacts share the account timeline on that relay. Contact read positions remain independent. |
| GroupManager | Group ID |
| ChannelManager | Channel ID |

## Interpret status

A resource starts at `idle`. A full pass reports `synchronizing`, then `caughtUp` only after reaching the observed remote head and committing the required processing. For groups, readable ciphertext must also be processed. Missing required keys report `blocked` with a missing-key reason while timeline ingestion and key recovery can continue. Epochs before membership do not block completion. Individually rejected messages remain diagnostics rather than unfinished work.

`caughtUp` remains observable between passes. It does not promise that no newer remote events exist.

Backward history pages, publication confirmation, and group management-only reads do not establish whole-resource completion.

Only an actual failure to complete synchronization reports `blocked`; notification/subscription failures alone do not block successful HTTP synchronization.

Known connection, authentication, permission, verification, storage, and history failures carry a reason. Unclassified failures retain the original `error` with an unknown reason.

A new attempt clears the current block and error; failures never advance `lastSynchronizedAt`.

### Status lifetime

`lastSynchronizedAt` records the last successful full pass in this component instance. It is absent before completion and resets with a new instance. Stopping background work resets its observations to `idle`, but preserves the latest explicit `synchronize()` pass and its subsequent status events. Canceling a pass reports `idle` while retaining the previous successful time. Runtime states are not persisted.

## Retention and history limits

`hasRetentionGap` is independent of progress: `caughtUp` with a known retention gap is valid. Account-message gaps are restored from existing storage and remain sticky. Group/channel read pages do not currently expose a retention-gap flag, so false means no known gap, not proof of complete history. A missing history range is a blocking reason only when it prevents processing from completing.

[All guides](../README.md)
