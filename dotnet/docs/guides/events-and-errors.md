# Events and errors

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

Source: [Integration.cs](../../examples/Meshline.Sdk.Examples/Integration.cs). To observe the earliest synchronization events, attach handlers before starting the client. UI applications should dispatch state updates onto their UI thread. Keep handlers short, avoid blocking the component's background work, and observe exceptions from any tasks the application starts in response.

Events indicate changes; local queries provide the durable current view. Use `ConversationChanged` for the conversation list, contact events for contacts and requests, channel events for followed timelines, and group events for membership, applications, timelines, and key recovery. Reopening the database does not replay every historical UI event.

## Choose events by task

| Emitter | Events | Application use |
| --- | --- | --- |
| Client | `ConversationChanged` | Re-query the affected local conversation. |
| Account/device/profile managers | `AccountChanged`, `DeviceChanged`, `DeviceStateChanged`, `ProfileChanged` | Refresh route, authorization, or profile views. |
| Message manager | `MessageReceived`, `TimelineChanged`, `SendStatusChanged` | Display received messages and track outgoing operations. |
| Message manager | `ContactChanged`, `ContactRequestChanged` | Refresh contacts and pending requests. |
| Channel manager | `ChannelChanged`, `TimelineChanged`, `FollowChanged` | Refresh channel metadata, posts, or follow state. |
| Group manager | `GroupChanged`, `TimelineChanged`, `ApplicationsChanged`, `KeyRecoveryChanged` | Refresh membership, readable history, admission, or key recovery. |
| Message/group/channel managers | `SyncStatusChanged` | Observe [resource progress](synchronization.md#observe-local-progress); startup alone does not establish completion. |
| All client components | `StateChanged`, `BackgroundError` | Observe lifecycle and background failures. |

The coordinated client forwards its managers' background errors. When composing
managers independently, subscribe to each component. Ordinary .NET event handlers
run synchronously on the calling thread, including background threads. Handle
application callback failures locally; do not assume every event isolates observer
exceptions. Never synchronously wait for component operations inside a handler.
Track asynchronous work started by the application separately from SDK shutdown.

## Cancellation and error types

Awaited failures propagate to the caller; background failures carry the operation,
resource, and exception in `BackgroundErrorEventArgs`.

| Exception | Meaning and response |
| --- | --- |
| `ArgumentException` / `ArgumentOutOfRangeException` | Correct invalid arguments or range bounds. |
| `InvalidOperationException` / `ObjectDisposedException` | Check initialization, authorization, and component lifetime. |
| `RelayException` | Inspect the original `Error.Code`, `Error.Message`, and `Error.Data`; distinguish business rejection from transport interruption. |
| `TimeoutException` | An SDK-owned request deadline expired; inspect the diagnostic fields below. |
| `OperationCanceledException` | Caller/component cancellation or a dependency cancellation; it is not itself evidence of an SDK deadline. |
| HTTP, socket, or storage exception | Keep the original exception and retained state; fix the failing dependency. |

SDK-owned Registry RPC, Relay HTTP, and WebSocket deadlines throw `TimeoutException`.
`Data["operation"]` identifies the request, for example `registry.getversion` or
`relay.http.message.send`; `Data["timeoutSeconds"]` is its budget. `InnerException`
preserves the cancellation that ended the I/O. For budgets and HTTP configuration,
see [transport](transport-and-di.md#request-timeouts-and-cancellation).

Caller cancellation and component/pool disposal remain cancellation. Application-owned
HTTP clients and signers can impose independent timeouts; inspect their original
exceptions. DNS, TLS, and other transport failures retain their original causes.
A timeout or cancellation does not prove a remote mutation was rejected. Reconcile
the persisted operation before retrying; [canceling a send wait](direct-messages.md#wait-for-an-acceptance-milestone)
does not cancel the send.

For diagnosis by symptom, see [troubleshooting](troubleshooting.md).

## API reference

[BackgroundErrorEventArgs](../api/Meshline.Components.BackgroundErrorEventArgs.md) · [BackgroundOperation](../api/Meshline.Components.BackgroundOperation.md) · [RelayException](../api/Meshline.Transport.RelayException.md) · [ProtocolViolation](../api/Meshline.Validation.ProtocolViolation.md)
