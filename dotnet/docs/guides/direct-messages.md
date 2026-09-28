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

Source: [Messaging.cs](../../samples/Meshline.Sdk.Examples/Messaging.cs). `SendMessageAsync` queues a local outbox operation and returns `MessageSendStatus`. The running component submits pending messages. Retain the message ID to query `GetSendStatusAsync` and observe `SendStatusChanged` for progress.

## Interpret send status

| State | Application interpretation |
| --- | --- |
| `Queued` | Persisted locally and waiting for submission. |
| `Submitting` | Submission is in progress. |
| `SubmissionUnknown` | A submission was attempted; acceptance is not yet known. |
| `RelayAccepted` | The submitting relay accepted the message for delivery. |
| `TargetAccepted` | The destination relay accepted it. This is not a recipient read receipt. |
| `Failed` | The outgoing operation failed definitively. Inspect its error. |
| `Canceled` | Locally canceled before confirmed relay acceptance. |

`None` and `All` are filter values, not additional progress stages. The SDK retries pending operations; an application should not turn an uncertain outcome into another `SendMessageAsync` call, which creates a separate outgoing message. `CancelMessageAsync` only changes a message while it is still `Queued`, returning `false` for missing messages and other states. Cancellation is not a remote recall of submitted or accepted content.

An outbox entry is removed after `TargetAccepted`. `GetSendStatusAsync` then returns `null` because no local outbox entry remains; `null` alone does not identify a delivery failure or prove delivery. Observe the status event and use stored messages for history rather than treating the outbox as a permanent delivery ledger.

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

Account messages are validated, deduplicated across relays, and stored in a local sequence. Contacts must be authorized at receive time. Storage or network failures should be diagnosed from the exposed errors while retaining pending state for retry.

## API reference

[MessageManager](../api/Meshline.Components.MessageManager.md) · [DirectMessageDraft](../api/Meshline.Models.Client.DirectMessageDraft.md) · [MessageSendStatus](../api/Meshline.Models.Client.MessageSendStatus.md) · [MessageSendState](../api/Meshline.Models.Client.MessageSendState.md) · [MessageBody](../api/Meshline.Models.Protocol.MessageBody.md) · [ContentReference](../api/Meshline.Models.Protocol.ContentReference.md)
