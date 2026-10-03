# Quick start

[Guide index](../README.md) · [Application integrations](integrations.md)

## Prerequisites and installation

Target `net10.0` and install the .NET 10 SDK. From your application directory:

```sh
dotnet add package Meshline.Sdk
```

For source development, use a project reference to the SDK instead of also referencing the NuGet package. The [SDK README](../../README.md#requirements-and-installation) shows that command.

Before connecting, obtain a `NetworkContext` and registered relays from your intended Meshline network. Explicitly select `IAccountSigner` and `IRelayRegistry` implementations: the SDK supplies optional NEP-6 wallet and Neo RPC adapters, or you can provide your own. Supply `ISecretProtector` as described in [Application integrations](integrations.md). Choose a writable database path dedicated to this network/account/device. No public network or integration is selected automatically.

## Establish a new account

The following method opens a new account session and invokes your application's asynchronous loop. The loop ends when the user signs out or the application exits. Use the imports in [Sessions.cs](../../examples/Meshline.Sdk.Examples/Sessions.cs): `Meshline`, `Meshline.Interactions`, `Meshline.Models.Client`, `Meshline.Storage`, and `Meshline.Transport`.

<!-- snippet: new-account -->
```csharp
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
```
<!-- /snippet -->

`MigrateAsync` creates or upgrades the schema. `InitializeAsync` loads component state; it does not migrate storage. `EstablishAccountAsync` selects an eligible relay, creates and authorizes the device, and publishes the initial route. Supply `AccountEstablishmentOptions.RelayId` when relay selection must be explicit. Interrupted establishment can resume with the same database and selected relay.

The client is disposed before the pool because `await using` declarations unwind in reverse order. The application retains ownership of its signer and protector. Keep the background-error handler connected for the entire session.

## Continue with messaging

Inside `runApplication`, establish [contact authorization](profiles-and-contacts.md) before [sending a direct message](direct-messages.md). A returned send status identifies a persisted outbox operation; observe its later state instead of displaying an immediate delivery or read receipt.

For an existing authorized device, reopen its database with the same protector and [start the existing session](lifecycle.md#reopen-an-authorized-device). An already established account without a valid local device requires [explicit recovery](recovery-and-migration.md); do not call recovery as a generic response to network errors.

## API reference

[MeshlineClient](../api/Meshline.MeshlineClient.md) · [ClientOptions](../api/Meshline.Models.Client.ClientOptions.md) · [AccountEstablishmentOptions](../api/Meshline.Models.Client.AccountEstablishmentOptions.md) · [MeshlineDatabase](../api/Meshline.Storage.MeshlineDatabase.md)
