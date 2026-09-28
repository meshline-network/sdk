# Events and troubleshooting

[Guide index](../README.md) · [Lifecycle](lifecycle.md)

## Observe background work

Awaited operations propagate their failures to the caller. Background failures are reported through `BackgroundError`, including the operation, resource when available, and exception. Subscribe for the session's lifetime. The following example demonstrates symmetric event subscription and unsubscription around an application-controlled lifetime:

<!-- snippet: events -->
```csharp
public static async Task ObserveAsync(
    MeshlineClient client,
    Func<CancellationToken, Task> waitForSessionEnd,
    CancellationToken cancellationToken = default)
{
    void OnReceived(object? sender, MessageReceivedEventArgs args)
    {
        foreach (var message in args.Messages)
            Console.WriteLine($"{message.Key.Sender}: {message.Body?.Text}");
    }
    void OnStatus(object? sender, MessageSendStatusChangedEventArgs args) =>
        Console.WriteLine($"{args.Status.MessageId}: {args.Status.State}");
    void OnError(object? sender, BackgroundErrorEventArgs args) =>
        Console.Error.WriteLine($"{args.Operation} ({args.Resource}): {args.Error}");

    client.MessageManager.MessageReceived += OnReceived;
    client.MessageManager.SendStatusChanged += OnStatus;
    client.BackgroundError += OnError;
    try
    {
        await waitForSessionEnd(cancellationToken);
    }
    finally
    {
        client.MessageManager.MessageReceived -= OnReceived;
        client.MessageManager.SendStatusChanged -= OnStatus;
        client.BackgroundError -= OnError;
    }
}
```
<!-- /snippet -->

Source: [Integration.cs](../../samples/Meshline.Sdk.Examples/Integration.cs). To observe the earliest synchronization events, attach handlers before starting the client. UI applications should dispatch state updates onto their UI thread. Keep handlers short, avoid blocking the component's background work, and observe exceptions from any tasks the application starts in response.

Events indicate changes; local queries provide the durable current view. Use `ConversationChanged` for the conversation list, contact events for contacts and requests, channel events for followed timelines, and group events for membership, applications, timelines, and key recovery. Reopening the database does not replay every historical UI event.

## Diagnose the actual boundary

| Symptom or error | Check and response |
| --- | --- |
| SQLite open, schema, or binding failure | Confirm the directory, permissions, migration, and account/network identity. Preserve the database while correcting the cause. |
| Protected secrets cannot be restored | Reuse the original protection keys and purpose binding. A fresh protector with unrelated keys does not repair persisted secrets. |
| Startup reports missing device authorization | Inspect the route, certificate validity, and published device state; deliberately choose renewal or recovery when needed. |
| `HttpRequestException` or socket failure | Inspect relay endpoints, proxy/handler configuration, cancellation, and network availability. Background polling and reconnect may recover; keep diagnostics visible. |
| `RelayException` | Inspect its structured `Error`, including the protocol code and message. Resolve role, capacity, state conflict, or authorization requirements before retrying. |
| Invalid signature, protocol data, or JSON | Verify network/account binding and the received input. Do not accept invalid data as an empty successful response. |
| `OperationCanceledException` | Distinguish user cancellation from component/session lifetime cancellation and request timeout; it does not alone prove that a remote mutation was rejected. |
| Pending message has an unknown outcome | Query the original outbox operation and let the SDK reconcile it. Sending a new message creates a different operation. |
| List does not show a recent update | Dispose the old snapshot reader and open a new query after the relevant event. |
| Contact synchronization rejects future timestamps | Check the local clock and the five-minute contact-record tolerance. |
| Group message cannot be decrypted | Check local secret availability and group membership, then consider group-key recovery. Account recovery alone does not restore all history. |

## Reconnection and retry

WebSocket notifications are an acceleration path for HTTP reads. Startup does not block until they are available. The SDK reconnects, restores subscriptions, and performs a catch-up read for newly confirmed subscription sets; periodic HTTP polling continues during notification trouble.

Do not delete storage, establish another account, or silently invoke recovery as a general retry policy. Retained migration progress, outbox operations, and synchronization cursors enable safe resumption. Use explicit application cancellation for work the user abandons and await shutdown so owned work can drain.

When reporting a problem, include the SDK version, operation, structured error, and reproduction steps. Remove private keys, protected-secret blobs, message plaintext, and bearer/session credentials from logs shared outside the application.

## API reference

[BackgroundErrorEventArgs](../api/Meshline.Components.BackgroundErrorEventArgs.md) · [BackgroundOperation](../api/Meshline.Components.BackgroundOperation.md) · [RelayException](../api/Meshline.Transport.RelayException.md) · [ProtocolViolation](../api/Meshline.Validation.ProtocolViolation.md)
