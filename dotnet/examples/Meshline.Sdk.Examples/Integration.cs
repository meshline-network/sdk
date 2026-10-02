using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Meshline.Examples;

public static class Integration
{
    #region options
    public static ClientOptions CreateOptions(IRelayRegistry registry, IAccountSigner signer)
    {
        var options = new ClientOptions { Context = registry.Context, AccountId = signer.AccountId };
        options.Validate();
        return options;
    }
    #endregion

    #region events
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
    #endregion

    #region http
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
    #endregion

    #region di
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
    #endregion
}
