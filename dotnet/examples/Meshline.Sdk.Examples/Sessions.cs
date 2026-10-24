using Meshline.Interactions;
using Meshline.Models;
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
    #endregion

    #region recovery
    public static async Task RecoverAsync(
        MeshlineClient initializedClient,
        string relayId,
        CancellationToken cancellationToken = default)
    {
        // The client was constructed with an account signer and has not started.
        await initializedClient.EstablishAccountAsync(
            new AccountEstablishmentOptions { RelayId = relayId }, cancellationToken);
    }
    #endregion

    #region establishment-selection
    public static async Task<DeviceSelectionInfo?> EstablishOrGetSelectionAsync(
        MeshlineClient client, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.EstablishAccountAsync(cancellationToken: cancellationToken);
            return null;
        }
        catch (AccountEstablishmentException error)
            when (error.Reason == AccountEstablishmentReason.DeviceSelectionRequired)
        {
            // Return the verified list to the application. It owns the user's choice.
            return error.DeviceSelection;
        }
    }

    public static Task ReplaceSelectedDeviceAsync(
        MeshlineClient client, string selectedDeviceId,
        CancellationToken cancellationToken = default) =>
        client.EstablishAccountAsync(new AccountEstablishmentOptions
        {
            ReplaceDeviceId = selectedDeviceId
        }, cancellationToken);
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

    #region push-gateway-options
    public static Task EstablishPushEnabledAccountAsync(
        MeshlineClient client, string pushGateway, CancellationToken cancellationToken = default) =>
        client.EstablishAccountAsync(new AccountEstablishmentOptions
        {
            PushGateway = pushGateway
        }, cancellationToken);
    #endregion

    #region push-device-creation
    public static Task<Meshline.Models.Protocol.DeviceCertificate> CreatePushEnabledDeviceAsync(
        MeshlineClient client, string pushGateway, CancellationToken cancellationToken = default) =>
        client.DeviceManager.CreateDeviceAsync(new DeviceCreationOptions
        {
            Validity = TimeSpan.FromDays(365),
            PushGateway = pushGateway
        }, cancellationToken);
    #endregion

    #region push-account-recovery
    public static Task RecoverPushEnabledAccountAsync(
        MeshlineClient client, string pushGateway, CancellationToken cancellationToken = default) =>
        client.RecoverAccountAsync(new AccountRecoveryOptions
        {
            PushGateway = pushGateway
        }, cancellationToken);
    #endregion

    #region push-gateway-update
    public static async Task RenewDeviceWithGatewayAsync(
        MeshlineClient client,
        FieldUpdate<string> pushGateway,
        TimeSpan validity,
        CancellationToken cancellationToken = default)
    {
        // Use an initialized, stopped client constructed with an account signer.
        // Pass default to retain the gateway, a URL to set it,
        // or FieldUpdate<string>.Delete to remove the declaration.
        await client.DeviceManager.RenewDeviceAsync(new DeviceRenewalOptions
        {
            Validity = validity,
            PushGateway = pushGateway
        }, cancellationToken);
        // Publish and confirm the exact local certificate through the resumable workflow.
        await client.EstablishAccountAsync(cancellationToken: cancellationToken);
    }
    #endregion
}
