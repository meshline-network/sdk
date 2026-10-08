using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class RouteCacheTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static int Queries(TestClient fixture) => fixture.Relay.Requests.Count(request => request.Method == "account.route.resolve");

    [Fact]
    public async Task Publication_populates_cache_and_hits_do_not_notify_or_query()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        var queries = Queries(fixture);
        var changes = 0;
        manager.AccountChanged += (_, _) => changes++;

        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(59));
        for (var i = 0; i < 10; i++)
            Assert.Same(manager.Route, await manager.GetRouteAsync(cancellationToken: Token));

        Assert.Equal(queries, Queries(fixture));
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Cold_concurrent_reads_share_one_verified_lookup()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var queries = Queries(fixture);

        var reads = Enumerable.Range(0, 12).Select(_ => fixture.Client.AccountManager.GetRouteAsync(peer.AccountId, Token)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.All(reads, read => Assert.False(read.IsCompleted));
        Assert.Equal(queries + 1, Queries(fixture));
        release.SetResult();
        var routes = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.All(routes, route => Assert.Equal(peer.AccountId, route!.Account));
        Assert.Equal(queries + 1, Queries(fixture));
        Assert.Equal(fixture.Account.AccountId, fixture.Client.Route!.Account);
    }

    [Fact]
    public async Task Different_accounts_do_not_wait_for_each_others_network_request()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var first = new AccountSigner();
        using var second = new AccountSigner();
        fixture.Relay.AddPeer(first);
        fixture.Relay.AddPeer(second);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve" && RequestQuery.Parse(request)["account"] == first.AccountId)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };

        var pending = fixture.Client.AccountManager.GetRouteAsync(first.AccountId, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var route = await fixture.Client.AccountManager.GetRouteAsync(second.AccountId, Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(second.AccountId, route!.Account);
        Assert.False(pending.IsCompleted);
        release.SetResult();
        Assert.Equal(first.AccountId, (await pending)!.Account);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_reads_return_immediately_and_refresh_only_notifies_for_changed_content(bool newer)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        var route = manager.Route!;
        var resolved = route with { Revision = route.Revision + (newer ? 1 : 0) };
        resolved = resolved with { AccountSignature = [.. fixture.Account.Sign(resolved.GetAccountSigningInput(TestNetwork.Context))] };
        fixture.Relay.Routes[fixture.Account.AccountId] = fixture.Relay.SignRoute(resolved);
        var changes = 0;
        manager.AccountChanged += (_, _) => changes++;
        var queries = Queries(fixture);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        fixture.Relay.Clock.Advance(TimeSpan.FromHours(1));

        var read = manager.GetRouteAsync(cancellationToken: Token);
        Assert.True(read.IsCompletedSuccessfully);
        Assert.Same(route, await read);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        for (var i = 0; i < 10; i++)
            Assert.Same(route, await manager.GetRouteAsync(cancellationToken: Token));
        var refresh = manager.RefreshRouteAsync(cancellationToken: Token);
        Assert.False(refresh.IsCompleted);
        release.SetResult();
        await refresh;

        Assert.Equal(queries + 1, Queries(fixture));
        Assert.Equal(newer ? 1 : 0, changes);
        Assert.Equal(resolved.Revision, (await manager.GetRouteAsync(cancellationToken: Token))!.Revision);
        Assert.Equal(queries + 1, Queries(fixture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_missing_refresh_preserves_cache_and_defers_access_triggered_retry(bool notFound)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        var original = manager.Route;
        var errors = 0;
        manager.BackgroundError += (_, _) => Interlocked.Increment(ref errors);
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.resolve")
                return notFound ? Task.FromResult(OfflineRelay.Error("not_found")) : throw new HttpRequestException("offline");
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        fixture.Relay.Clock.Advance(TimeSpan.FromHours(1));
        if (notFound)
            Assert.Null(await manager.RefreshRouteAsync(cancellationToken: Token));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => manager.RefreshRouteAsync(cancellationToken: Token));
        var queries = Queries(fixture);

        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(59));
        for (var i = 0; i < 10; i++)
            Assert.Same(original, await manager.GetRouteAsync(cancellationToken: Token));
        Assert.Equal(queries, Queries(fixture));
        Assert.Equal(AccountState.Established, manager.State);
        Assert.True(notFound || errors > 0);

        fixture.Relay.Handler = null;
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Same(original, await manager.GetRouteAsync(cancellationToken: Token));
        await AsyncTest.UntilAsync(() => Queries(fixture) == queries + 1);
        await manager.StopAsync(Token);
    }

    [Fact]
    public async Task Forced_refresh_bypasses_freshness_and_failure_cooldown()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        var queries = Queries(fixture);
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.resolve"
            ? throw new HttpRequestException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.RefreshRouteAsync(cancellationToken: Token));
        Assert.Equal(queries + 1, Queries(fixture));

        fixture.Relay.Handler = null;
        Assert.NotNull(await manager.RefreshRouteAsync(cancellationToken: Token));
        Assert.Equal(queries + 2, Queries(fixture));
    }

    [Fact]
    public async Task Expiration_is_a_hard_limit_even_during_failure_cooldown()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        await manager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromSeconds(30), cancellationToken: Token);
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.resolve"
            ? throw new HttpRequestException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.RefreshRouteAsync(cancellationToken: Token));
        var queries = Queries(fixture);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetRouteAsync(cancellationToken: Token));
        Assert.Equal(queries, Queries(fixture));
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "account.route.resolve"
            ? OfflineRelay.Error("not_found") : fixture.Relay.Respond(request));
        Assert.Null(await manager.RefreshRouteAsync(cancellationToken: Token));
        Assert.Equal(AccountState.Unknown, manager.State);
    }

    [Fact]
    public async Task Canceling_first_cold_waiter_does_not_cancel_shared_discovery()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.AddPeer(fixture.Account);
        var manager = fixture.Client.AccountManager;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var sharedToken = CancellationToken.None;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                sharedToken = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = manager.GetRouteAsync(cancellationToken: caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var second = manager.GetRouteAsync(cancellationToken: Token);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(sharedToken.IsCancellationRequested);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.NotNull(await second);
        Assert.Equal(1, Queries(fixture));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Stop_and_disposal_drain_refreshes_even_when_cancellation_callbacks_fail(bool start, bool dispose, bool failCallback)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        if (start)
            await manager.StartAsync(Token);
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var drained = AsyncTest.Signal();
        var callbackError = new InvalidOperationException("injected cancellation callback failure");
        CancellationTokenRegistration callback = default;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                using var registration = callback = failCallback ? token.Register(() => throw callbackError) : default;
                entered.TrySetResult();
                try { await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, release.Task); }
                finally { drained.TrySetResult(); }
            }
            return fixture.Relay.Respond(request);
        };
        fixture.Relay.Clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await manager.GetRouteAsync(cancellationToken: Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var refresh = failCallback ? manager.RefreshRouteAsync(cancellationToken: Token) : null;
        var stopping = dispose ? manager.DisposeAsync().AsTask() : manager.StopAsync(Token);
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await callback.DisposeAsync();
            Assert.False(stopping.IsCompleted);
            Assert.False(drained.Task.IsCompleted);
            if (!dispose && start)
                Assert.Equal(ComponentState.Stopping, manager.LifecycleState);
            if (!dispose && refresh is not null)
                Assert.False(refresh.IsCompleted);
        }
        finally { release.TrySetResult(); }

        if (failCallback)
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Contains(callbackError, error.Flatten().InnerExceptions);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh!);
        }
        else
            await stopping.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(drained.Task.IsCompleted);

        if (dispose)
        {
            Assert.Equal(ComponentState.Disposed, manager.LifecycleState);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.GetRouteAsync(cancellationToken: Token));
        }
        else
        {
            fixture.Relay.Handler = null;
            await manager.StartAsync(Token);
            Assert.NotNull(await manager.RefreshRouteAsync(cancellationToken: Token));
            await manager.StopAsync(Token);
        }
    }

    [Fact]
    public async Task Disposing_cold_discovery_cancels_all_waiters()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var manager = fixture.Client.AccountManager;
        var entered = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return fixture.Relay.Respond(request);
        };
        var first = manager.GetRouteAsync(cancellationToken: Token);
        var second = manager.GetRouteAsync(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Older_inflight_discovery_cannot_overwrite_new_publication(bool notFound)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        var previous = manager.Route!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var changes = 0;
        manager.AccountChanged += (_, _) => changes++;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return notFound ? OfflineRelay.Error("not_found") : OfflineRelay.Json(previous);
            }
            return fixture.Relay.Respond(request);
        };

        var refresh = manager.RefreshRouteAsync(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var published = await manager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(refresh.IsCompleted);
        release.SetResult();
        Assert.Equal(published, await refresh);

        Assert.Equal(published, await manager.GetRouteAsync(cancellationToken: Token));
        Assert.Equal(1, changes);
        await using var database = fixture.Database.Open();
        Assert.Equal(published.Revision, (await database.AccountRoutes.SingleAsync(Token)).Revision);
    }

    [Theory]
    [InlineData("account-signature")]
    [InlineData("relay-signature")]
    [InlineData("conflict")]
    public async Task Invalid_refresh_does_not_poison_verified_cache(string variation)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        var previous = manager.Route!;
        var invalid = variation switch
        {
            "account-signature" => previous with { AccountSignature = [.. new byte[64]] },
            "relay-signature" => previous with { RelaySignature = [.. new byte[64]] },
            _ => previous with { ExpiresAt = previous.ExpiresAt + 1 }
        };
        if (variation == "conflict")
        {
            invalid = invalid with { AccountSignature = [.. fixture.Account.Sign(invalid.GetAccountSigningInput(TestNetwork.Context))] };
            invalid = fixture.Relay.SignRoute(invalid);
        }
        fixture.Relay.Routes[fixture.Account.AccountId] = invalid;

        var failure = await Record.ExceptionAsync(() => manager.RefreshRouteAsync(cancellationToken: Token));

        Assert.IsAssignableFrom<Exception>(failure);
        Assert.True(failure is HttpRequestException or InvalidDataException);
        Assert.Same(previous, await manager.GetRouteAsync(cancellationToken: Token));
        await using var database = fixture.Database.Open();
        Assert.Equal(previous.ToJson(), (await database.AccountRoutes.SingleAsync(Token)).DocumentJson);
    }

    [Fact]
    public async Task Reopened_component_revalidates_persisted_route_before_caching()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var queries = Queries(fixture);
        await fixture.ReopenAsync();

        Assert.NotNull(await fixture.Client.AccountManager.GetRouteAsync(cancellationToken: Token));
        Assert.NotNull(await fixture.Client.AccountManager.GetRouteAsync(cancellationToken: Token));

        Assert.Equal(queries + 1, Queries(fixture));
    }

    [Fact]
    public async Task Separate_components_do_not_share_cached_routes_for_the_same_account()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using var manager = new AccountManager(fixture.Relay.Options(fixture.Account), database.Options, fixture.Pool);
        await manager.InitializeAsync(Token);
        var queries = Queries(fixture);

        Assert.NotNull(await manager.GetRouteAsync(cancellationToken: Token));
        Assert.Equal(queries + 1, Queries(fixture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mismatched_network_or_registry_is_rejected(bool registry)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var context = registry ? TestNetwork.Context with { Registry = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
            : TestNetwork.Context with { Reference = TestNetwork.Context.Reference + 1 };
        await using var manager = new AccountManager(new() { Context = context, AccountId = fixture.Account.AccountId }, fixture.Database.Options, fixture.Pool);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InitializeAsync(Token));
    }
}
