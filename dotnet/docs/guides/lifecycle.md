# Lifecycle and ownership

[Guide index](../README.md) · [Accounts and devices](accounts-and-devices.md)

## Session order

Construction performs no I/O. Create the database directory and call `MeshlineDatabase.MigrateAsync`, construct the pool and client, attach event handlers, and call `InitializeAsync`. Then establish or recover authorization if needed, call `StartAsync`, run the application, and call `StopAsync` before asynchronous disposal.

Use one active `MeshlineClient` per database, including across processes. Concurrent
calls to that client use its component coordination. Await the old client's
`DisposeAsync` before handing the database to a replacement; stopping background
work alone does not end its foreground operations. Independently composed
components follow the same single-owner rule. The SDK does not enforce exclusive
database ownership.

| Operation | Meaning |
| --- | --- |
| `InitializeAsync` | Load local state once. A failed initialization leaves the component uninitialized so its cause can be corrected before retrying. |
| `StartAsync` | Start component background work after authorization checks. Completion does not wait for WebSocket readiness or prove that all history has synchronized. |
| `StopAsync` | Stop background processing and drain pending refreshes, including those triggered by reads before start or after a previous stop. It drains runtime device sessions and preserves the database for a later session. |
| `DisposeAsync` | End the component lifetime, release owned resources, and drain active work. A disposed component cannot be reused. |

Observe `LifecycleState` and `StateChanged` for lifecycle changes. Pass cancellation tokens to awaited operations. Shutdown itself must still be awaited when the application's ordinary operation token is canceled; the examples call `StopAsync` without that canceled token.

Verified local-device removal, rejected local authentication, or an own-state query
still denied after device-session renewal suspends the coordinated runtime. Observe
`BackgroundError` for the cause and `StateChanged` for completed shutdown. Suspension
preserves the last verified state and readable local history. Network failures,
certificate expiry and peer/recipient authorization errors do not establish
revocation. Background reads never request wallet signatures; deliberate
reauthorization belongs to `EstablishAccountAsync`.

Normal stop and authentication suspension drain device sessions across all relays,
including notifications, reconnects and renewal tasks. Account sessions and verified
relay metadata remain cached. A later start authenticates a fresh device session;
no device authentication continues in the stopped runtime. Stopping before runtime
attachment leaves setup sessions alone.

Shutdown attempts every child and releases its subscriptions and sessions even if
another cleanup task fails. It raises the completed running-to-stopped transition
before reporting errors to the explicit caller, or through `BackgroundError` for
automatic suspension. Multiple failures are retained in `AggregateException`.
If storage prevented saving an interrupted send's status, correct the problem and
reopen the client to reconcile its retained message ID and ciphertext.

Disposal requests lifetime cancellation before waiting for initialization/startup,
including startup waiting for account establishment. Concurrent disposals share
cancellation and serialize cleanup. Always await disposal: cancellation alone does
not mean active work has drained. After an error, `Disposed` is terminal and repeated
disposal is harmless; if resource cleanup failed before reaching that state, correct
the cause and retry disposal. New tracked operations remain blocked.

## Reopen an authorized device

Use the existing database, matching account/network options, and the same protection keys. This method intentionally has no account signer and performs no account-establishment or recovery write:

Initialization makes local history available without a connection. Start the
application immediately after initialization; start the connection independently.
`StartAsync` still awaits required relay authentication and may fail offline.
The example retries transient network failures with capped backoff, reports every
failure, and stops retrying on authorization, key, or storage errors. Replace its
console diagnostic with the application's connection/error presentation. It never
uses establishment as an automatic error fallback, and does not restart a runtime
after a confirmed revocation. Canceling the session drains pending startup/retry
work before disposing the client.

<!-- snippet: existing-device -->
```csharp
public static async Task RunExistingDeviceAsync(
    ClientOptions options,
    DatabaseOptions database,
    IRelayRegistry registry,
    ISecretProtector protector,
    Func<MeshlineClient, CancellationToken, Task> runApplication,
    CancellationToken cancellationToken = default)
{
    await MeshlineDatabase.MigrateAsync(database, cancellationToken);
    await using var pool = new RelayClientPool(options, registry);
    await using var client = new MeshlineClient(options, database, pool, protector);
    client.BackgroundError += (_, error) =>
        Console.Error.WriteLine($"{error.Operation}: {error.Error}");

    await client.InitializeAsync(cancellationToken);
    using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var connection = StartConnectionAsync();
    try
    {
        // Local history is available while connection/startup is still pending.
        await runApplication(client, session.Token);
    }
    finally
    {
        await session.CancelAsync();
        try { await connection; }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        finally { await client.StopAsync(); }
    }

    async Task StartConnectionAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await client.StartAsync(session.Token);
                return;
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Replace this diagnostic with the application's connection/error UI.
                Console.Error.WriteLine($"Startup: {error}");
                if (error is not (HttpRequestException or TimeoutException)
                    && error is not RelayException { Error.Code: "temporarily_unavailable" or "bad_gateway" or "rate_limited" })
                    return;
            }
            // Retry only transient startup failures. Authorization/key failures
            // need explicit user action; never call establishment from this loop.
            await Task.Delay(delay, session.Token);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }
}
```
<!-- /snippet -->

Source: [Sessions.cs](../../examples/Meshline.Sdk.Examples/Sessions.cs). The database's parent directory must exist. If startup fails, inspect the actual error and [authorization state](accounts-and-devices.md) before deciding whether recovery is appropriate.

## Resource ownership

`MeshlineClient` owns its six managers. Dispose the client before the application-owned `RelayClientPool`, and dispose a caller-supplied `HttpClient` after that pool. Readers from local queries own SQLite snapshot transactions and must also be disposed promptly. The signer, registry, and protector remain application-owned.

Independently composed components follow their constructor dependencies. Initialize and start dependencies before dependents; stop and dispose dependents first. The coordinated client's order is account, device, profile, messages, channels, then groups; shutdown reverses that order. Prefer the coordinated client unless the application needs to manage these relationships explicitly.

Starting activates background work; it does not wait for resources to catch up. See [synchronization](synchronization.md) for foreground refreshes, polling, notifications, and progress snapshots.

`MeshlineClient.StartAsync` waits for an executing `EstablishAccountAsync` before
starting its children. Once startup begins, establishment that would change
authorization requires stopping synchronization, even before the facade reports
Running. Canceling a startup wait does not cancel the establishment operation.

During runtime, a verified complete device list which removes the local device
causes MeshlineClient to stop all six managers asynchronously. Observe StateChanged
for the transition to Stopped. Local history remains readable after that transition.
Failed queries, unknown authorization and certificate expiry alone do not trigger
this removal policy. Explicit establishment and account signing are required to
reauthorize; StartAsync never republishes device authorization. Applications using
standalone managers own the coordination of their runtime shutdown.

## API reference

[ClientComponent](../api/Meshline.Components.ClientComponent.md) · [ComponentState](../api/Meshline.Components.ComponentState.md) · [MeshlineClient](../api/Meshline.MeshlineClient.md) · [RelayClientPool](../api/Meshline.Transport.RelayClientPool.md)
