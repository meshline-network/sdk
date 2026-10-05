# Lifecycle and ownership

[Guide index](../README.md) · [Accounts and devices](accounts-and-devices.md)

## Session order

Construction performs no I/O. Create the database directory and call `MeshlineDatabase.MigrateAsync`, construct the pool and client, attach event handlers, and call `InitializeAsync`. Then establish or recover authorization if needed, call `StartAsync`, run the application, and call `StopAsync` before asynchronous disposal.

| Operation | Meaning |
| --- | --- |
| `InitializeAsync` | Load local state once. A failed initialization leaves the component uninitialized so its cause can be corrected before retrying. |
| `StartAsync` | Start component background work after authorization checks. Completion does not wait for WebSocket readiness or prove that all history has synchronized. |
| `StopAsync` | Stop background processing and drain runtime work. It preserves the database for a later session. |
| `DisposeAsync` | End the component lifetime, release owned resources, and drain active work. A disposed component cannot be reused. |

Observe `LifecycleState` and `StateChanged` for lifecycle changes. Pass cancellation tokens to awaited operations. Shutdown itself must still be awaited when the application's ordinary operation token is canceled; the examples call `StopAsync` without that canceled token.

## Reopen an authorized device

Use the existing database, matching account/network options, and the same protection keys. This method intentionally has no account signer and performs no account-establishment or recovery write:

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
    try
    {
        await client.StartAsync(cancellationToken);
        await runApplication(client, cancellationToken);
    }
    finally
    {
        await client.StopAsync();
    }
}
```
<!-- /snippet -->

Source: [Sessions.cs](../../examples/Meshline.Sdk.Examples/Sessions.cs). The database's parent directory must exist. If startup fails, inspect the actual error and [authorization state](accounts-and-devices.md) before deciding whether recovery is appropriate.

## Resource ownership

`MeshlineClient` owns its six managers. Dispose the client before the application-owned `RelayClientPool`, and dispose a caller-supplied `HttpClient` after that pool. Readers from local queries own SQLite snapshot transactions and must also be disposed promptly. The signer, registry, and protector remain application-owned.

Independently composed components follow their constructor dependencies. Initialize and start dependencies before dependents; stop and dispose dependents first. The coordinated client's order is account, device, profile, messages, channels, then groups; shutdown reverses that order. Prefer the coordinated client unless the application needs to manage these relationships explicitly.

Starting activates background work; it does not wait for resources to catch up. See [synchronization](synchronization.md) for foreground refreshes, polling, notifications, and progress snapshots.

## API reference

[ClientComponent](../api/Meshline.Components.ClientComponent.md) · [ComponentState](../api/Meshline.Components.ComponentState.md) · [MeshlineClient](../api/Meshline.MeshlineClient.md) · [RelayClientPool](../api/Meshline.Transport.RelayClientPool.md)
