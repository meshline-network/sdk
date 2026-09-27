using Meshline.Interactions;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Meshline.Tests.Transport;

public sealed class DependencyInjectionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("none")]
    [InlineData("unkeyed")]
    [InlineData("unrelated")]
    public async Task Missing_pool_named_client_resolves_without_io_or_using_other_clients(string registration)
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var services = new ServiceCollection();
        services.AddSingleton(relay.Options(account));
        services.AddSingleton<IRelayRegistry>(relay);
        if (registration == "unkeyed")
            services.AddSingleton<HttpClient>(_ => throw new InvalidOperationException("Do not resolve the unkeyed client."));
        else if (registration != "none")
            services.AddKeyedSingleton<HttpClient>(registration, (_, _) => throw new InvalidOperationException("Do not resolve another named client."));
        services.AddScoped<RelayClientPool>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<RelayClientPool>();
        var second = secondScope.ServiceProvider.GetRequiredService<RelayClientPool>();

        Assert.NotSame(first, second);

        await first.GetAsync(relay.RelayId, Token);
        var client = await second.GetAsync(relay.RelayId, Token);
        await firstScope.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.GetAsync(relay.RelayId, Token));
        Assert.Same(client, await second.GetAsync(relay.RelayId, Token));
        Assert.Equal(0, relay.RegistryReads);
        Assert.Empty(relay.Requests);
        Assert.Empty(relay.Upgrades);
    }

    [Fact]
    public async Task Named_client_factory_failure_is_not_replaced_with_a_default_client()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var services = new ServiceCollection();
        var expected = new InvalidOperationException("Invalid named client configuration");
        services.AddSingleton(relay.Options(account));
        services.AddSingleton<IRelayRegistry>(relay);
        services.AddKeyedSingleton<HttpClient>("RelayClientPool", (_, _) => throw expected);
        services.AddScoped<RelayClientPool>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        await using var scope = provider.CreateAsyncScope();

        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<RelayClientPool>()));
        Assert.Empty(relay.Requests);
        Assert.Empty(relay.Upgrades);
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Singleton)]
    public async Task Constructor_injects_named_client_for_http_and_websocket_and_obeys_di_ownership(ServiceLifetime lifetime)
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var services = new ServiceCollection();
        services.AddSingleton(relay.Options(account));
        services.AddSingleton<IRelayRegistry>(relay);
        services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new UnexpectedHandler());
        services.AddHttpClient("unrelated").ConfigurePrimaryHttpMessageHandler(() => new UnexpectedHandler()).AddAsKeyed(lifetime);
        services.AddHttpClient(RelayClientPool.HttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new BorrowedHandler(relay)).SetHandlerLifetime(Timeout.InfiniteTimeSpan).AddAsKeyed(lifetime);
        services.AddScoped<RelayClientPool>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        await using var scope = provider.CreateAsyncScope();
        var pool = scope.ServiceProvider.GetRequiredService<RelayClientPool>();
        var http = scope.ServiceProvider.GetRequiredKeyedService<HttpClient>(RelayClientPool.HttpClientName);

        Assert.Same(pool, scope.ServiceProvider.GetRequiredService<RelayClientPool>());
        Assert.Empty(relay.Requests);
        Assert.Empty(relay.Upgrades);

        var client = await pool.GetAsync(relay.RelayId, account, Token);
        relay.SocketHandler = (request, socket) =>
        {
            socket.Reply(request.Id!, "{\"sequence\":9}");
            return Task.CompletedTask;
        };

        Assert.Equal(relay.RelayId, (await client.GetInfoAsync(Token)).RelayId);
        Assert.Equal(9, (await client.SendWebSocketAsync<SequenceResult>("probe", cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token)).Sequence);
        Assert.Single(relay.Upgrades);

        var peer = Assert.Single(relay.Sockets);
        // Scope disposal must drain active sessions and dispose a scoped client, while
        // a singleton client remains available until the root container is disposed.
        await scope.DisposeAsync();
        await peer.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.True(peer.ClientStreamDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.GetAsync(relay.RelayId, Token));

        if (lifetime == ServiceLifetime.Scoped)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => http.GetAsync("https://relay.test/relay/info", Token));
        else
        {
            using var response = await http.GetAsync("https://relay.test/relay/info", Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => http.GetAsync("https://relay.test/relay/info", Token));
    }

    [Fact]
    public void Container_rejects_singleton_pool_capturing_scoped_named_client()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var services = new ServiceCollection();
        services.AddSingleton(relay.Options(account));
        services.AddSingleton<IRelayRegistry>(relay);
        services.AddHttpClient(RelayClientPool.HttpClientName).AddAsKeyed();
        services.AddSingleton<RelayClientPool>();
        var error = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        }));

        Assert.Contains("Cannot consume scoped service", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(relay.Requests);
        Assert.Empty(relay.Upgrades);
    }

    // The test fixture owns its handler; the factory owns and can dispose this wrapper.
    sealed class BorrowedHandler(HttpMessageHandler handler) : HttpMessageHandler
    {
        readonly HttpMessageInvoker invoker = new(handler, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => invoker.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                invoker.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class UnexpectedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("The pool selected the default or an unrelated HTTP client.");
    }
}
