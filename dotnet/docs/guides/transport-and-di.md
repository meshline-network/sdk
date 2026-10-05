# Transport and dependency injection

[Guide index](../README.md) · [Lifecycle](lifecycle.md)

## Pools and relay sessions

Share one `RelayClientPool` among components for a single network/account/device session. Account and device authentication use separate pooled sessions. The first device-authenticated request binds the pool to that device; do not share it across independent devices or accounts.

`GetClientAsync` discovers a relay and acquires an authenticated session using an account or device signer. Returned clients are pool-owned. `RelayClient.GetDescriptorAsync` and `GetInfoAsync` read validated metadata. `SendHttpAsync` and `SendWebSocketAsync` expose lower-level protocol calls for applications that need them; preserve the operation's authentication, model validation, and response semantics.

Observe `PoolChanged`, `RelayChanged`, or a relay client's `StateChanged`. `ConnectionState` and `AuthenticationState` describe different things. HTTP connection state reflects observed communication rather than a continuously monitored connection. `SessionMode` is bound after successful authentication and remains bound after session expiry; authentication state also reflects expiry.

When using protocol models directly, `ProtocolModel.FromJson<T>` enforces JSON representation rules but does not call `Validate`. Perform the relevant field validation and cryptographic verification, including current signer authorization, before trusting received data. A model's successful field validation alone is not a verified signature. The higher-level components perform their workflow's validation and evidence checks.

## Supply an HTTP client

The pool creates and owns an HTTP client when none is supplied. Pass one explicitly to configure proxies or handlers. That client handles HTTP requests and WebSocket handshakes; the SDK owns the individual `ClientWebSocket` instances.

<!-- snippet: http -->
```csharp
public static async Task WithHttpAsync(
    ClientOptions options, IRelayRegistry registry,
    Func<RelayClientPool, CancellationToken, Task> runSession,
    CancellationToken cancellationToken = default)
{
    using var http = new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    { Timeout = Timeout.InfiniteTimeSpan };
    await using var pool = new RelayClientPool(options, registry, http);
    // runSession must dispose its client before returning.
    await runSession(pool, cancellationToken);
}
```
<!-- /snippet -->

Source: [Integration.cs](../../examples/Meshline.Sdk.Examples/Integration.cs). The SDK does not change a supplied client's configuration. Disable automatic redirects, cookies, and implicit retry handlers. An infinite timeout leaves deadlines to SDK cancellation and request handling; finite HTTP timeouts can end requests and WebSocket handshakes sooner. Long-lived socket traffic uses session cancellation and request deadlines.

## Register Microsoft DI services

Add `Microsoft.Extensions.Http` 10.x to the application. Register the named client `RelayClientPool.HttpClientName` and enable keyed registration with `AddAsKeyed`. This example reopens one previously authorized account/device after its database has been migrated:

<!-- snippet: di -->
```csharp
public static async Task RunWithDependencyInjectionAsync(
    ClientOptions options, DatabaseOptions database, IRelayRegistry registry,
    ISecretProtector protector,
    Func<MeshlineClient, CancellationToken, Task> runApplication,
    CancellationToken cancellationToken = default)
{
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddSingleton<IRelayRegistry>(registry);
    services.AddHttpClient(RelayClientPool.HttpClientName,
            http => http.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
        .AddAsKeyed(ServiceLifetime.Singleton);
    services.AddScoped<RelayClientPool>();

    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var pool = scope.ServiceProvider.GetRequiredService<RelayClientPool>();
    // Existing, authorized device; migrate this database before opening the session.
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

`AddHttpClient(name)` alone does not enable keyed injection. If no matching keyed client exists, the pool's optional constructor argument is `null` and it creates its own client. Unkeyed clients and other names are not selected. Errors resolving an explicitly registered keyed client propagate.

The example uses a singleton named client and one scoped pool, and disposes the application client before its scope. Multi-account applications must provide the correct options and registry per account scope rather than reusing this example's singleton account binding. A scoped keyed HTTP client also works when it shares the pool's scope. Dispose scopes asynchronously so relay sessions drain before the HTTP client is released.

## Notification shutdown and recovery

Stopping a component clears its subscriptions while leaving pooled relay clients
available to other components. Each component allows up to five seconds in total
for pending subscription requests and the final clear. If a sent request has an
unknown outcome or clearing fails, the SDK closes that WebSocket connection.
Components still running reconnect, restore subscriptions, and catch up over HTTP.
The same reset applies to subscription timeouts during normal operation.

## Request timeouts and cancellation

Relay requests use a 60-second SDK deadline. Registry RPC requests use
`RpcRelayRegistryOptions.RequestTimeout`, which defaults to 15 seconds. HTTP
deadlines include response body reads; WebSocket request deadlines include
connection readiness. A caller-supplied HTTP client can impose a shorter budget.

SDK deadlines produce `TimeoutException`; caller cancellation remains cancellation.
See [events and errors](events-and-errors.md#cancellation-and-error-types) for diagnostic
fields and dependency failures. A timeout does not prove a write failed remotely;
reconcile persisted state before retrying. Notification reconnection continues
after a request timeout; transport recovery does not replay business requests.

## API reference

[RelayClientPool](../api/Meshline.Transport.RelayClientPool.md) · [RelayClient](../api/Meshline.Transport.RelayClient.md) · [RelayConnectionState](../api/Meshline.Transport.RelayConnectionState.md) · [RelayAuthenticationState](../api/Meshline.Transport.RelayAuthenticationState.md)
