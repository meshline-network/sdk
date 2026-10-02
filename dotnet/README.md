# Meshline .NET SDK

**Version 1.0.0** · **.NET 10** · Assembly: `Meshline.Sdk` · Root namespace: `Meshline`

[NuGet package](https://www.nuget.org/packages/Meshline.Sdk) · [Release notes](https://github.com/meshline-network/sdk/releases/tag/v1.0.0) · [Source](https://github.com/meshline-network/sdk)

[Developer guide](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/README.md) · [Complete API reference](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/api/README.md) · [Compilable examples](https://github.com/meshline-network/sdk/blob/main/dotnet/examples/README.md)

Meshline is a decentralized messaging and social protocol built around self-sovereign identity. Its .NET SDK provides the client workflows and protocol primitives for building applications on a Meshline network:

- Account routes, home-relay migration, device authorization, and profiles.
- Contacts, encrypted direct messages, channels, and encrypted groups.
- HTTP and WebSocket relay access, authentication, session renewal, and notification recovery.
- SQLite persistence, pending-operation recovery, local conversations, and snapshot queries.
- Protocol models, Canonical JSON, signature verification, and message encryption.

Use `MeshlineClient` to coordinate the client components, or compose the components independently. Construction performs no I/O. Applications supply account signing, relay registry access, and platform-specific secret protection.

## Requirements and installation

Install the .NET 10 SDK and target `net10.0` in the consuming application. Add the NuGet package:

```sh
dotnet add package Meshline.Sdk --version 1.0.0
```

For source integration, reference the SDK project instead, replacing the paths below with your project and checkout locations:

```sh
dotnet add path/to/MyApp.csproj reference path/to/sdk/dotnet/src/Meshline.Sdk/Meshline.Sdk.csproj
```

SQLite and its EF Core provider are included. The application needs a writable directory for each local database and access to its chosen Meshline network. A named HTTP client registered through Microsoft DI additionally requires `Microsoft.Extensions.Http` 10.x; see [HTTP configuration](#http-configuration).

Version 1.0.0 is validated by the [offline test suite](#validation-scope). Live-relay interoperability and cross-platform release validation, including mobile, browser, and NativeAOT environments, remain pending. Relay-server/DHT behavior and application content rendering are outside the SDK test suite.

## Application integrations

Implement these interfaces from `Meshline.Interactions`:

| Interface | Application responsibility |
| --- | --- |
| [`IAccountSigner`](https://github.com/meshline-network/sdk/blob/main/dotnet/src/Meshline.Sdk/Interactions/IAccountSigner.cs) | Expose the account identifier and matching public key, and sign the supplied bytes using the account's signing mechanism. |
| [`IRelayRegistry`](https://github.com/meshline-network/sdk/blob/main/dotnet/src/Meshline.Sdk/Interactions/IRelayRegistry.cs) | Supply the `NetworkContext`, resolve a relay by ID, and enumerate relay entries for that network. |
| [`ISecretProtector`](https://github.com/meshline-network/sdk/blob/main/dotnet/src/Meshline.Sdk/Interactions/ISecretProtector.cs) | Protect and unprotect local secrets using the supplied purpose string and the application's platform-specific key protection. |

`ClientOptions.Context` must match the registry's network, and `ClientOptions.AccountId` must identify the signer's account. A `NetworkContext` identifies the Neo network reference and registry script hash; obtain these values from the network you intend to use.

An account signer is needed for account establishment, recovery, route publication, and device authorization. A client with a previously authorized local device can be constructed without an account signer for device-authorized operations. Preserve access to the same secret-protection keys when reopening its database.

## Quick start

This example establishes a **new account** and runs an application session. Pass your three integrations, a database path dedicated to this network/account/device, and an asynchronous application loop. The loop receives the running client and returns when the session should end.

```csharp
using Meshline;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Transport;

public static class MeshlineExample
{
    public static async Task RunNewAccountAsync(
        IRelayRegistry registry,
        IAccountSigner accountSigner,
        ISecretProtector secretProtector,
        string databasePath,
        Func<MeshlineClient, CancellationToken, Task> runApplication,
        CancellationToken cancellationToken = default)
    {
        var options = new ClientOptions
        {
            Context = registry.Context,
            AccountId = accountSigner.AccountId
        };
        var database = new DatabaseOptions { Path = databasePath };
        Directory.CreateDirectory(Path.GetDirectoryName(database.Path)!);
        await MeshlineDatabase.MigrateAsync(database, cancellationToken);

        await using var pool = new RelayClientPool(options, registry);
        await using var client = new MeshlineClient(
            options, database, pool, secretProtector, accountSigner);

        client.BackgroundError += (_, error) =>
            Console.Error.WriteLine($"{error.Operation}: {error.Error}");

        await client.InitializeAsync(cancellationToken);
        await client.EstablishAccountAsync(cancellationToken: cancellationToken);
        await client.StartAsync(cancellationToken);
        try
        {
            await runApplication(client, cancellationToken);
        }
        finally
        {
            await client.StopAsync();
        }
    }
}
```

`EstablishAccountAsync` selects an eligible relay through the registry. Pass `AccountEstablishmentOptions.RelayId` to choose one explicitly. It creates and authorizes the local device and publishes the account route; interrupted establishment can resume against the same database and relay. The client is disposed before the application-owned pool because `await using` declarations are released in reverse order.

For an **existing account on the same device**, use its existing database and secret protector, initialize the client, and call `StartAsync` without `EstablishAccountAsync`. Startup requires a valid, published authorization for the local device. Initialization does not implicitly create or migrate the database.

Background synchronization reads data over HTTP. WebSocket notifications trigger earlier reads, while periodic HTTP polling continues if the notification connection or a subscription is unavailable. Connection failures are reported through `BackgroundError`; the SDK reconnects and restores channel and group subscriptions in the background. Each newly confirmed or restored subscription set triggers an HTTP catch-up read; an unchanged set on the same connection does not trigger repeated subscription requests. Starting the client does not wait for WebSocket readiness.

For **explicit account recovery**, initialize the client with an account signer and call `RecoverAccountAsync` before starting it. `AccountRecoveryOptions` can specify a relay, previous device state, and revision values when needed. Recovery publishes device state and an account route; choose it deliberately when restoring access rather than as a routine startup fallback. If resuming with a previously authorized device, keep its database and protected secrets together.

## Common operations

The following snippets run inside your application session with a running `client` and a `cancellationToken`. Subscribe to events before starting the client if the application needs notifications from the start of synchronization. Events may arrive from background work; UI applications should dispatch updates to their UI thread.

### Contacts and direct messages

`MessageManager` owns contact invitations, requests, acceptance, aliases, removal, and synchronization. Use `AddContactAsync` to send a request and `AcceptContactRequestAsync` to accept an incoming request. Establish the required contact authorization before sending direct messages to another account.

Subscribe to incoming messages and send-state changes:

```csharp
client.MessageManager.MessageReceived += (_, args) =>
{
    foreach (var message in args.Messages)
        Console.WriteLine($"{message.Key.Sender}: {message.Body?.Text}");
};
client.MessageManager.SendStatusChanged += (_, args) =>
    Console.WriteLine($"{args.Status.MessageId}: {args.Status.State}");
```

With `recipientAccountId` set to an authorized contact's account identifier, queue a text message:

```csharp
using Meshline.Models.Client;
using Meshline.Models.Protocol;

var status = await client.MessageManager.SendMessageAsync(
    recipientAccountId,
    new DirectMessageDraft
    {
        Body = new MessageBody { ContentType = "text/plain", Text = "Hello from Meshline!" }
    },
    cancellationToken);
Console.WriteLine($"{status.MessageId}: {status.State}");
```

The returned `MessageSendStatus` describes the local outbox operation, not confirmed recipient delivery. The running component submits queued messages and retries pending operations. Observe `SendStatusChanged`, query `GetSendStatusAsync`, or read `GetOutboxAsync` to track progress. `GetMessageHistoryAsync` reads stored direct messages; `CancelMessageAsync` attempts to cancel an outbox operation.

Contact changes and received messages commit with the relay synchronization position. Contact record validation permits update times at most five minutes ahead of the local clock. An account contact synchronization batch exceeding this tolerance is rejected and reported through `BackgroundError`, while subsequent messages continue to be processed. A message without the required local contact authorization is discarded; later contact synchronization does not replay it. Network, storage, and cancellation failures leave an unfinished entry available for retry. Verified account messages have one local sequence, preserve each relay's order, and are deduplicated across relays. `GroupManager` consumes this stored stream with its own durable cursor and atomically commits group changes with that cursor.

### Conversations and local queries

`MeshlineClient` combines direct messages, followed channels, and groups into local conversations. Query them in batches:

```csharp
await using var reader = await client.GetConversationsAsync(
    cancellationToken: cancellationToken);
while (true)
{
    var conversations = await reader.ReadNextAsync(50, cancellationToken);
    if (conversations.Count == 0)
        break;
    foreach (var conversation in conversations)
        Console.WriteLine($"{conversation.ConversationId}: {conversation.UnreadCount} unread");
}
```

`ConversationChanged` reports updates; `MarkReadAsync` updates a conversation's read position. Local list queries return `QueryReader<T>` and accept explicit filters. Each reader holds a fixed SQLite snapshot, so later changes do not reorder its batches. An empty batch marks the end. Open a new reader to refresh and dispose readers promptly to release their transactions. Relay-backed history and administration queries use their protocol paging cursors instead.

### Components

`MeshlineClient` exposes these components:

| Component | Responsibilities |
| --- | --- |
| `AccountManager` | Resolve and publish account routes. |
| `DeviceManager` | Create, renew, authorize, and remove devices; implement `IDeviceSigner`. |
| `ProfileManager` | Resolve and update account profiles; `client.Profile` exposes the current profile. |
| `MessageManager` | Contacts, direct messages, outbox state, and account-message synchronization. |
| `ChannelManager` | Channel creation, publishing, editing, deletion, following, history, and closure. |
| `GroupManager` | Encrypted group messages, membership, roles, invitations, key rotation/recovery, and closure. |

For independently assembled components, follow their constructor dependencies. For example, `ProfileManager` uses an `AccountManager` and an `IDeviceSigner`, which can be the same component group's `DeviceManager`. Pass that group one shared `RelayClientPool`.

## Lifecycle and ownership

- Call `MeshlineDatabase.MigrateAsync` explicitly before initializing components. Constructors only configure objects; `InitializeAsync` prepares component state, and `StartAsync` begins background work after account/device authorization.
- `MeshlineClient` initializes and starts its components in dependency order, then stops and disposes them in reverse order. Applications composing components independently must follow the same ordering.
- `StopAsync` ends background work and subscriptions while retaining shared relay clients in the pool. Each component allows up to five seconds in total for pending subscription requests and the final clear. If the outcome of a sent subscription request is unknown, or the final clear fails, the SDK closes that WebSocket connection; components still running reconnect, restore their subscriptions, and catch up over HTTP. The same connection reset applies to subscription timeouts during normal operation. Components can be started again. `DisposeAsync` drains active operations and releases their resources.
- The application owns the pool. Stop and dispose all clients/components using it before disposing it. Supplied signers, registries, and secret protectors remain application-managed integrations.
- Observe `StateChanged` for lifecycle changes and `BackgroundError` for background failures. Awaited operation failures propagate to the caller.

## Local storage and migrations

SQLite is the built-in storage engine; other database providers are not supported. Pass `DatabaseOptions` to clients and components. The SDK configures EF Core internally, so no provider registration or `DbContextOptions` is needed. `DatabaseOptions.Path` resolves a relative path against the current working directory when assigned, without opening the database.

Use a separate database for each network, account, and device. The application chooses and creates the parent directory and calls `MeshlineDatabase.MigrateAsync` to create or upgrade the database. Startup never migrates or deletes it automatically. `ISecretProtector` protects stored device and group secrets; it does not encrypt the entire SQLite database.

The existing `InitialCreate` migration is the **1.0.0 database baseline**. Pre-1.0 development databases built from a different initial migration are not covered by this upgrade baseline. Preserve the database and the matching secret-protection keys when moving or restoring an application's local state.

## Relay connections

Create one `RelayClientPool` per account and device. `ClientOptions` supplies the network and account; the device is bound when first used for authentication. Components share the pool's relay clients without returning or disposing them.

Relay clients begin without a session. The pool reuses or authenticates a suitable client when requested, keeping account and device sessions separate. Public requests can use a client in any mode. Device-session WebSocket calls start notification dispatch. Each opened stream has one dispatcher that raises `NotificationReceived` and continues receiving even if no component handlers remain. Disconnects trigger reconnection without replaying business requests.

`RelayClientPool.Clients` exposes the current collection. Observe `PoolChanged` for collection changes and `RelayChanged` for updates to a client's `RelayId`, `SessionMode`, `ConnectionState`, `AuthenticationState`, or `LastError`. `SessionMode` stays null until authentication succeeds and remains bound after expiry. HTTP connection state reflects observed communication, not a continuously monitored socket; reading authentication state also reflects session expiry. Use `GetDescriptorAsync`, `GetInfoAsync`, `SendHttpAsync`, and `SendWebSocketAsync` for lower-level access.

### HTTP configuration

Without a supplied `HttpClient`, each pool creates and owns its own client. To configure proxies, logging handlers, or an offline peer, pass a client as the optional third constructor argument. Here, `options` and `registry` are the same account/network integrations used above:

```csharp
using Meshline.Transport;

using var http = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
}) { Timeout = Timeout.InfiniteTimeSpan };

await using var pool = new RelayClientPool(options, registry, http);
// Construct and run the client here; dispose it before leaving this scope.
```

The same HTTP client sends requests and establishes WebSocket connections. The SDK owns each `ClientWebSocket`. A supplied HTTP client remains caller-owned and must outlive the pool; a pool-owned client is disposed after its relay sessions and pending authentication tasks exit.

The SDK does not change a supplied client's configuration. Disable redirects, cookies, and implicit request retries in its handler pipeline. An infinite HTTP timeout leaves deadlines to SDK cancellation tokens; a finite timeout can end HTTP requests and WebSocket handshakes sooner. Long-lived WebSocket traffic uses session cancellation and request deadlines.

### Dependency injection

For Microsoft DI, reference `Microsoft.Extensions.Http` 10.x in the consuming application. Register a named client using `RelayClientPool.HttpClientName` (`"RelayClientPool"`) and enable its keyed registration with `AddAsKeyed`:

```csharp
using Meshline.Interactions;
using Meshline.Transport;
using Microsoft.Extensions.DependencyInjection;

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
// Run the client here and dispose it before the scope ends.
```

This configuration describes one account/network. Each scope owns a pool for an account/device session while the named HTTP client is shared. Multi-account applications must provide the correct `ClientOptions` and registry in each account scope. A scoped keyed HTTP client also works when it shares the pool's scope. Dispose scopes asynchronously so relay sessions drain before DI releases the HTTP client. Connection lifetime refreshes connections without rotating handlers beneath the long-lived client.

`AddHttpClient(name)` alone configures the factory but does not enable keyed injection. If no matching keyed client is registered, the optional constructor argument is null and the pool creates its own client. Unkeyed clients or other names are not selected. Errors resolving a registered keyed client propagate to the caller.

## Use the source project

To use a local checkout, run from this directory (`dotnet/`):

```sh
dotnet restore Meshline.Sdk.slnx
dotnet build Meshline.Sdk.slnx -c Release --no-restore
```

Then add the [project reference](#requirements-and-installation) to your application. The NuGet package is the usual installation path when you do not need to modify the SDK.

The package includes XML API documentation for IntelliSense, covering parameters, return values, lifecycle, ownership, validation, and paging constraints. The [developer guide](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/README.md) expands the workflows into chapters, and the [API reference](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/api/README.md) presents the public surface as generated Markdown. Both follow the source checkout; consult the matching release tag when integrating a different package version.

## Validation scope

Version 1.0.0 passed 542 offline tests covering protocol and cryptographic vectors, transport, SQLite persistence, account and device workflows, contacts, messaging, channels, groups, and lifecycle behavior. These tests use in-memory relay peers and isolated local databases.

This does not establish live-relay interoperability or support for mobile, browser, or NativeAOT environments. Applications remain responsible for account signing, network registry access, secret protection, content retrieval, and rendering.

The [test guide](https://github.com/meshline-network/sdk/blob/main/dotnet/TESTING.md) describes the coverage and protocol-vector provenance. For SDK contributions, local checks, and package releases, see the [maintenance guide](https://github.com/meshline-network/sdk/blob/main/MAINTENANCE.md).

## Source layout

The [source project](https://github.com/meshline-network/sdk/tree/main/dotnet/src/Meshline.Sdk) contains the client, components, interactions, protocol/client models, identity, serialization, validation, storage, and transport in one assembly. The [test project](https://github.com/meshline-network/sdk/tree/main/dotnet/tests/Meshline.Sdk.Tests) contains the offline suite, organized by Protocol, Transport, Storage and domain-specific Components, with reusable Support helpers and an EF Core design-time factory. Public types and method signatures are available in the source alongside the examples above.

## Source and license

- [Source repository](https://github.com/meshline-network/sdk)
- [NuGet package](https://www.nuget.org/packages/Meshline.Sdk)
- [Meshline developer resources](https://meshline.org/en/resources)
- [Meshline protocol](https://github.com/meshline-network/protocol)
- [Registry reference contracts](https://github.com/meshline-network/contracts)
- [MIT license](https://github.com/meshline-network/sdk/blob/main/LICENSE); the full license text is also included in the NuGet package as `LICENSE`.
