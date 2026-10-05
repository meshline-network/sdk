# Synchronization

[Guide index](../README.md) · [Lifecycle](lifecycle.md) · [Storage and pagination](storage-and-pagination.md)

## Choose how to refresh

| Application task | API or behavior |
| --- | --- |
| Read what is already stored | Local query readers; they do not synchronize or mark read. |
| Keep an active session updated | `StartAsync` runs background synchronization, subscriptions, and polling. |
| Await a fresh account timeline pass | `MessageManager.SynchronizeAsync(relayId, cancellationToken)` |
| Await a fresh group pass | `GroupManager.SynchronizeAsync(groupRef, cancellationToken)` |
| Await a fresh channel pass | `ChannelManager.SynchronizeAsync(channelRef, cancellationToken)` |
| Inspect progress without network I/O | The manager's `GetSyncStatusAsync(resourceId)` and `SyncStatusChanged`. |

HTTP reads transfer synchronized data. WebSocket notifications trigger earlier reads;
periodic polling continues when notifications are unavailable. Newly confirmed or
restored subscriptions trigger an HTTP catch-up read. Startup does not wait for
WebSocket readiness. Report connection failures through [background diagnostics](events-and-errors.md).

## Run a foreground pass

For a foreground refresh, call `MessageManager.SynchronizeAsync(relayId)`, `GroupManager.SynchronizeAsync(groupRef)`, or `ChannelManager.SynchronizeAsync(channelRef)`. Initialize the component and its dependencies and establish usable device authorization first; `StartAsync` is not required. The call performs a fresh pass even when the previous snapshot is `CaughtUp`. It serializes with background synchronization and returns an immutable `ResourceSyncStatus` for that pass, rather than reading a potentially newer pass's cached status.

<!-- snippet: synchronize -->
```csharp
public static async Task<ResourceSyncStatus> RefreshGroupAsync(
    MeshlineClient client, string relayId, GroupRef group,
    CancellationToken cancellationToken = default)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(TimeSpan.FromSeconds(30));
    await client.MessageManager.SynchronizeAsync(relayId, deadline.Token);
    return await client.GroupManager.SynchronizeAsync(group, deadline.Token);
}
```
<!-- /snippet -->

Source: [Synchronization.cs](../../examples/Meshline.Sdk.Examples/Synchronization.cs). The example refreshes account recovery messages before the group and bounds the combined work to 30 seconds. Inspect the returned `State`; only `CaughtUp` confirms completion of this pass. Then open a [local history reader](storage-and-pagination.md) to display stored messages.

## Completion and cancellation

The pass reads forward from persisted progress until a response reports no more pages, then completes required local processing. Groups also synchronize keys and process decryptable messages; missing keys return `Blocked` without waiting indefinitely for key recovery.

Operational exceptions update the resource status and propagate to the caller.

Cancellation retains committed pages, releases the synchronization gate, and leaves the next call able to resume. A queued canceled call does not start a pass.

Stopping background work does not cancel independent foreground calls; use their cancellation tokens or dispose the component.

Manual group synchronization first consumes pending group private-state and history-secret messages already stored in the account timeline. Call `MessageManager.SynchronizeAsync` for the relevant relay first to download newly arrived account recovery messages.

This is not a frozen remote snapshot or a wait for a fixed sequence captured at invocation.

A continuously growing timeline may prolong a pass; callers should supply a cancellation deadline.

Account synchronization is scoped to one relay, channel synchronization does not follow the channel, and group synchronization does not join a group or grant access to earlier epochs. One-shot calls do not start background workers or notification subscriptions.

## Observe local progress

The message, group, and channel managers expose `GetSyncStatusAsync(resourceId)` and `SyncStatusChanged`. Queries return local snapshots without network requests. Subscribe before querying to observe subsequent changes; event handlers must not synchronously wait for component operations. Component startup does not itself imply that resources have caught up.

| Manager | Resource identifier |
| --- | --- |
| MessageManager | Relay ID: all contacts share the account timeline on that relay. Contact read positions remain independent. |
| GroupManager | Group ID |
| ChannelManager | Channel ID |

## Interpret status

A resource starts at `Idle`. A full pass reports `Synchronizing`, then `CaughtUp` only after reaching the observed remote head and committing the required processing. For groups, readable ciphertext must also be processed. Missing required keys report `Blocked` with a missing-key reason while timeline ingestion and key recovery can continue. Epochs before membership do not block completion. Individually rejected messages remain diagnostics rather than unfinished work.

`CaughtUp` remains observable between passes. It does not promise that no newer remote events exist.

Backward history pages, publication confirmation, and group management-only reads do not establish whole-resource completion.

Only an actual failure to complete synchronization reports `Blocked`; notification/subscription failures alone do not block successful HTTP synchronization.

Known connection, authentication, permission, verification, storage, and history failures carry a reason. Unclassified failures retain the original `Error` with an unknown reason.

A new attempt clears the current block and error; failures never advance `LastSynchronizedAt`.

### Status lifetime

`LastSynchronizedAt` records the last successful full pass in this component instance. It is absent before completion and resets with a new instance. Stopping background work resets its observations to `Idle`, but preserves the latest explicit `SynchronizeAsync` pass and its subsequent status events. Canceling a pass reports `Idle` while retaining the previous successful time. Runtime states are not persisted.

## Retention and history limits

`HasRetentionGap` is independent of progress: `CaughtUp` with a known retention gap is valid. Account-message gaps are restored from existing storage and remain sticky. Group/channel read pages do not currently expose a retention-gap flag, so false means no known gap, not proof of complete history. A missing history range is a blocking reason only when it prevents processing from completing.

## API reference

[ResourceSyncStatus](../api/Meshline.Models.Client.ResourceSyncStatus.md) · [ResourceSyncState](../api/Meshline.Models.Client.ResourceSyncState.md) · [ResourceSyncBlockReason](../api/Meshline.Models.Client.ResourceSyncBlockReason.md)
