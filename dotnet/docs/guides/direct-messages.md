# Direct messages and the outbox

[Guide index](../README.md) · [Profiles and contacts](profiles-and-contacts.md)

## Queue encrypted content

Prerequisites: a running, authorized client, an established contact relationship, and the recipient's account ID. Keep the database and secret protector available for encryption, persistence, and pending-operation recovery.

<!-- snippet: direct-message -->
```csharp
public static async Task<MessageSendStatus> SendTextAsync(
    MeshlineClient client, string authorizedContactAccountId, string text,
    CancellationToken cancellationToken = default)
{
    var status = await client.MessageManager.SendMessageAsync(
        authorizedContactAccountId,
        new DirectMessageDraft
        {
            Body = new MessageBody { ContentType = "text/plain", Text = text }
        }, cancellationToken);
    Console.WriteLine($"{status.MessageId}: {status.State}");
    return status;
}
```
<!-- /snippet -->

Source: [Messaging.cs](../../examples/Meshline.Sdk.Examples/Messaging.cs). `SendMessageAsync` queues a local outbox operation and returns `MessageSendStatus`. The running component submits pending messages. Retain the message ID to query `GetSendStatusAsync` and observe `SendStatusChanged` for progress.

Before enqueueing, the SDK queries and verifies both accounts' device states concurrently (once for a self-send). Failure or cancellation prevents enqueueing. See [query reuse and freshness](accounts-and-devices.md#device-state-queries).

## Interpret send status

| State | Application interpretation |
| --- | --- |
| `Queued` | Persisted locally and waiting for submission. |
| `Submitting` | Submission is in progress. |
| `SubmissionUnknown` | A submission was attempted; acceptance is not yet known. |
| `RelayAccepted` | The submitting relay accepted the message for delivery. |
| `TargetAccepted` | The destination relay accepted it. This is not a recipient read receipt. |
| `Failed` | The outgoing operation failed definitively. Inspect its error. |
| `Canceled` | Canceled locally while still queued. |

`None` and `All` are filter values, not additional progress stages.

## Wait for an acceptance milestone

Use `WaitForSendStatusAsync(messageId, targetState, cancellationToken)` when a workflow
needs to continue after an outgoing message progresses. The default target is
`TargetAccepted`; the other supported targets are `Queued` and `RelayAccepted`.
Transient states, terminal failure states, and combined filter flags are invalid targets.

The method returns the actual status when the milestone has already been reached or
is reached later. `TargetAccepted` also satisfies a `RelayAccepted` wait. A `Queued`
wait accepts any recorded state. `Failed` and `Canceled` finish every wait, so always
check the returned `State` before treating the result as success. Target acceptance
does not mean the recipient has read or processed the message.

<!-- snippet: wait-for-send -->
```csharp
public static async Task<MessageSendStatus?> WaitForDeliveryAsync(
    MeshlineClient client, string messageId, CancellationToken cancellationToken = default)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(TimeSpan.FromSeconds(30));
    return await client.MessageManager.WaitForSendStatusAsync(
        messageId, cancellationToken: deadline.Token);
}
```
<!-- /snippet -->

Call this after `SendMessageAsync`, using its returned message ID. The example bounds
only the wait to 30 seconds. Cancellation throws `OperationCanceledException`; it
does not cancel, resend, or otherwise change the outgoing operation. Query the same
message ID or wait again afterward. The SDK has no built-in waiting timeout.

The component must be initialized. Waiting checks local state and observes commits
by that manager without polling or sending network requests. It does not start the
sender: keep the client or message manager running for delivery progress. Waits
survive stop/start; disposing the component cancels and drains them. Changes made
by another manager or process are not observed.

An unknown or already evicted record returns `null` immediately, with the same
uncertainty as `GetSendStatusAsync`. A wait already observing a pending message
receives its committed completion even if that transaction evicts the result from
terminal history. This does not recover outcomes evicted before observation began.

## Cancel or reconcile a send

`CancelMessageAsync` changes a message only while it is `Queued`, returning `false`
for missing messages or other states. It cannot recall submitted or accepted content.
Canceling a caller's operation is not evidence of a remote rollback.

The SDK retries and reconciles pending operations from the original database.
Keep that database and its protector. Query the original message ID after an
uncertain result; another `SendMessageAsync` call creates a separate message.

## Retained send history

The SDK retains the newest 1000 terminal outbox records (`TargetAccepted`, `Failed`, and `Canceled`) across this account, including internal protocol messages. Retention uses `CreatedAt` descending, then ordinal `MessageId` descending to break ties; it follows creation order, not completion order. Pending records are never evicted by this limit. Initialization and send-state transactions prune excess terminal records without removing message history. Retained records include the original encrypted request and remain queryable after restart through `GetSendStatusAsync`; `GetOutboxAsync` includes retained direct-message terminal records unless filtered out.

`GetSendStatusAsync` returns `null` for an unknown or evicted record. This does not identify a delivery failure, prove delivery, or distinguish eviction from a message never recorded locally. Previously discarded results cannot be reconstructed from message history alone. Observe status events for progress and use stored messages for history rather than treating the bounded outbox as a permanent delivery ledger.

<!-- snippet: outbox -->
```csharp
public static async Task PrintPendingMessagesAsync(
    MeshlineClient client, CancellationToken cancellationToken = default)
{
    await using var reader = await client.MessageManager.GetOutboxAsync(
        state: MessageSendState.Queued | MessageSendState.Submitting | MessageSendState.SubmissionUnknown,
        cancellationToken: cancellationToken);
    while (true)
    {
        var batch = await reader.ReadNextAsync(50, cancellationToken);
        if (batch.Count == 0) break;
        foreach (var message in batch)
            Console.WriteLine($"{message.MessageId}: {message.State}");
    }
}
```
<!-- /snippet -->

## Receive and display messages

Subscribe to `MessageReceived` before starting synchronization when early arrivals matter. Notifications may contain multiple messages and run on background threads; dispatch UI changes to the application's UI thread. Use `GetMessageAsync` for a specific message or `GetMessageHistoryAsync` for persisted history. Event handling and durable local queries complement each other; rebuild the UI from storage after reopening the application.

Message bodies and attachments have protocol validation rules. `text/plain` is suitable for simple text; the application is responsible for rendering content safely. `ContentReference` describes externally stored content and its metadata. It does not make the SDK an attachment uploader or downloader.

Account messages are validated, deduplicated across relays, and stored in a local sequence. `GetMessageHistoryAsync` returns messages in ascending `MessageInfo.LocalSequence` order, including when messages arrive with older creation times. The same persisted position is returned by `GetMessageAsync` and `MessageReceived`. Positions belong to this database's account-message stream, may have gaps, and cannot be compared across databases or with relay timeline sequences. `CreatedAt` remains available for display. Contacts must be authorized at receive time. A message discarded for missing authorization is not replayed by later contact synchronization. Storage or network failures should be diagnosed from the exposed errors while retaining pending state for retry.

For a fresh remote read, use [synchronization](synchronization.md). For local paging
and explicit read positions, see [storage and pagination](storage-and-pagination.md)
and [conversations](conversations.md).

## API reference

[MessageManager](../api/Meshline.Components.MessageManager.md) · [DirectMessageDraft](../api/Meshline.Models.Client.DirectMessageDraft.md) · [MessageSendStatus](../api/Meshline.Models.Client.MessageSendStatus.md) · [MessageSendState](../api/Meshline.Models.Client.MessageSendState.md) · [MessageBody](../api/Meshline.Models.Protocol.MessageBody.md) · [ContentReference](../api/Meshline.Models.Protocol.ContentReference.md)
