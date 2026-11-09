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

Source: [Messaging.cs](../../examples/Meshline.Sdk.Examples/Messaging.cs). `SendMessageAsync` queues a local outbox operation and returns `MessageSendStatus`. Direct messages automatically request offline push notifications. The running component submits pending messages. Retain the message ID to query `GetSendStatusAsync` and observe `SendStatusChanged` for progress.

New sends enter only the outbox. `GetOutboxAsync` returns `OutgoingMessageInfo`
items: `Message` contains the plaintext body, attachment references, and reply
reference; `Status` contains the message ID, recipient, creation time, and send
state. Both come from the same fixed database snapshot, without separate reads
for each message. `GetOutgoingMessageAsync(messageId)` returns the same complete
`OutgoingMessageInfo` for one direct-message outbox entry, or `null` if absent or
evicted. Its content and status are read together in one local query. Messages
enter history only after the
account timeline is synchronized and verified. Combine outbox items with history
by the current sender account and `Status.MessageId` so a send appears once. Outbox
entries have no `LocalSequence`; their display position is temporary until history
assigns one. Failure or cancellation
before synchronization does not create a history row. Source acceptance and
destination delivery are separate: once synchronized, a message remains history
even if delivery later fails. Synchronization does not terminate a pending send.

Sending reuses both accounts' valid device states and fetches necessary cache misses concurrently. Authorization checks still run; a necessary lookup failure or cancellation prevents enqueueing. See [metadata caching](accounts-and-devices.md#metadata-reads-and-refreshes).

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

`GetOutboxAsync` returns a fixed snapshot of direct messages matching its optional
recipient and send-state filters, ordered by creation time and then ordinal message
ID ascending. Each `OutgoingMessageInfo` contains plaintext in `Message` and send
metadata in `Status`. `GetOutgoingMessageAsync` reads the same information for one
message. Both reject missing or invalid content and exclude internal protocol
messages. Open a new reader to observe later changes.

The outbox retains the newest 1000 eligible terminal records (`TargetAccepted`,
`Failed`, `Canceled`) across the account, including internal protocol messages,
ordered by creation time and then ordinal message ID descending. Pending records
and source-accepted direct messages awaiting local history synchronization are
protected, regardless of their eventual delivery outcome. Completed internal
protocol messages remain eligible without local history. Initialization and
message transactions prune excess eligible records; message history is preserved.

`GetSendStatusAsync` reads status only, including internal operations. An unknown
or evicted record returns `null`; this says nothing about delivery success or
failure, and history cannot reconstruct an evicted outcome. Observe status events
for progress and use stored messages for lasting conversation history.

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
            Console.WriteLine($"{message.Status.MessageId}: {message.Status.State} — {message.Message.Body?.Text}");
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

[MessageManager](../api/Meshline.Components.MessageManager.md) · [DirectMessageDraft](../api/Meshline.Models.Client.DirectMessageDraft.md) · [OutgoingMessageInfo](../api/Meshline.Models.Client.OutgoingMessageInfo.md) · [MessageSendStatus](../api/Meshline.Models.Client.MessageSendStatus.md) · [MessageSendState](../api/Meshline.Models.Client.MessageSendState.md) · [MessageBody](../api/Meshline.Models.Protocol.MessageBody.md) · [ContentReference](../api/Meshline.Models.Protocol.ContentReference.md)
