using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Transport;

namespace Meshline.Examples;

public static class Sessions
{
    #region new-account
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
    #endregion

    #region existing-device
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
    #endregion

    #region recovery
    public static async Task RecoverAsync(
        MeshlineClient initializedClient,
        string relayId,
        CancellationToken cancellationToken = default)
    {
        // The client was constructed with an account signer and has not started.
        await initializedClient.RecoverAccountAsync(
            new AccountRecoveryOptions { RelayId = relayId }, cancellationToken);
    }
    #endregion

    #region migration
    public static Task MigrateHomeRelayAsync(
        MeshlineClient client,
        string targetRelayId,
        CancellationToken cancellationToken = default) =>
        client.ChangeHomeRelayAsync(targetRelayId, cancellationToken);
    #endregion

    #region device-renewal
    public static async Task RenewDeviceAsync(
        MeshlineClient client,
        TimeSpan validity,
        CancellationToken cancellationToken = default)
    {
        var route = client.Route ?? throw new InvalidOperationException("Resolve the account route first.");
        await client.DeviceManager.RenewDeviceAsync(validity, cancellationToken);
        var publication = await client.DeviceManager.PublishDeviceStateAsync(
            route.RelayId, cancellationToken: cancellationToken);
        Console.WriteLine($"Device-state publication: {publication.Status}");
    }
    #endregion
}
