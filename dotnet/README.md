# Meshline .NET SDK

**.NET 10** · Assembly: `Meshline.Sdk` · Root namespace: `Meshline`

[NuGet package](https://www.nuget.org/packages/Meshline.Sdk) · [Release notes](https://github.com/meshline-network/sdk/releases) · [Source](https://github.com/meshline-network/sdk)

[Developer guide](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/README.md) · [Complete API reference](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/api/README.md) · [Compilable examples](https://github.com/meshline-network/sdk/blob/main/dotnet/examples/README.md)

Meshline is a decentralized messaging and social protocol built around self-sovereign identity. Its .NET SDK provides the client workflows and protocol primitives for building applications on a Meshline network:

- Account routes, home-relay migration, device authorization, and profiles.
- Contacts, encrypted direct messages, channels, and encrypted groups.
- HTTP and WebSocket relay access, authentication, session renewal, and notification recovery.
- SQLite persistence, pending-operation recovery, local conversations, and snapshot queries.
- Protocol models, Canonical JSON, signature verification, and message encryption.

Use `MeshlineClient` to coordinate the client components, or compose the components independently. Construction performs no I/O. Applications supply account signing, relay registry access, and platform-specific secret protection.

The optional `RpcRelayRegistry` and `Nep6AccountSigner` adapters provide RPC registry access and NEP-6 wallet signing. Applications choose and register the adapters explicitly; platform-specific secret protection remains application-provided. See the [integration guide](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/integrations.md).

## Installation

Target `net10.0` and install the package:

```sh
dotnet add package Meshline.Sdk
```

SQLite and its EF Core provider are included. Each local account/device needs a
writable database directory and persistent secret-protection keys. For source
builds and project references, see [Maintenance](https://github.com/meshline-network/sdk/blob/main/MAINTENANCE.md).

## Quick start

This example establishes a **new account**. Supply a trusted registry, matching
account signer, persistent secret protector, a database path dedicated to this
network/account/device, and your application loop. See
[application integrations](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/integrations.md)
for their contracts.

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
        IAccountSigner signer,
        ISecretProtector protector,
        string databasePath,
        Func<MeshlineClient, CancellationToken, Task> runApplication,
        CancellationToken cancellationToken = default)
    {
        var options = new ClientOptions
        {
            Context = registry.Context,
            AccountId = signer.AccountId
        };
        var database = new DatabaseOptions { Path = Path.GetFullPath(databasePath) };
        Directory.CreateDirectory(Path.GetDirectoryName(database.Path)!);
        await MeshlineDatabase.MigrateAsync(database, cancellationToken);

        await using var pool = new RelayClientPool(options, registry);
        await using var client = new MeshlineClient(options, database, pool, protector, signer);
        client.BackgroundError += (_, error) =>
            Console.Error.WriteLine($"{error.Operation}: {error.Error}");

        await client.InitializeAsync(cancellationToken);
        await client.EstablishAccountAsync(cancellationToken: cancellationToken);
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
}
```

The compiled source is in [Sessions.cs](https://github.com/meshline-network/sdk/blob/main/dotnet/examples/Meshline.Sdk.Examples/Sessions.cs).
Establishment selects an eligible relay; pass `AccountEstablishmentOptions.RelayId`
to choose one. On an ordinary restart, initialize with the existing database and
protector, then start without establishing again. Choose account recovery explicitly
when authorization must be restored.

## Build your application

| Task | Guide |
| --- | --- |
| Open, stop, and reopen a session | [Quick start](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/quick-start.md) and [lifecycle](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/lifecycle.md) |
| Exchange contacts and messages | [Contacts](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/profiles-and-contacts.md) and [sending, waiting, and outbox status](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/direct-messages.md) |
| Refresh data and build message lists | [Synchronization](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/synchronization.md), [pagination](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/storage-and-pagination.md), and [read positions](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/conversations.md) |
| Add channels, groups, recovery, or custom transport | [All guides](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/README.md) |
| Diagnose a failure | [Events and errors](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/events-and-errors.md) and [troubleshooting](https://github.com/meshline-network/sdk/blob/main/dotnet/docs/guides/troubleshooting.md) |

## Version and validation scope

Guides and the generated API reference follow the source checkout. Use the matching
release tag for an installed package; APIs on `main` may not yet be published.
Packages embed their README at release time and include XML documentation for IntelliSense.

[Offline tests](https://github.com/meshline-network/sdk/blob/main/dotnet/TESTING.md)
cover protocol, transport, persistence, and component workflows against scripted
peers. Live-relay interoperability and cross-platform release validation, including
mobile, browser, and NativeAOT, remain pending. These tests do not validate relay-server
or DHT behavior. Applications own secret protection, content retrieval, and rendering.
The protocol remains a [Protocol 1.0 draft](https://meshline.org/protocol/v1/en/index.html).

[Maintenance and contributing](https://github.com/meshline-network/sdk/blob/main/MAINTENANCE.md) · [MIT license](https://github.com/meshline-network/sdk/blob/main/LICENSE)
