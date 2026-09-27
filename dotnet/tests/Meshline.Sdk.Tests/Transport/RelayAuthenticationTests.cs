using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using System.Net;

namespace Meshline.Tests.Transport;

public sealed class RelayAuthenticationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pool_reuses_public_clients_coalesces_authentication_and_separates_modes()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var unauthenticated = await pool.GetAsync(relay.RelayId, cancellationToken: Token);

        Assert.Empty(relay.Requests);

        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        relay.Handler = async (request, token) =>
        {
            if (request.Method == "auth.challenge")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            return relay.Respond(request);
        };
        var pending = Enumerable.Range(0, 16).Select(_ => pool.GetAsync(relay.RelayId, account, Token)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.All(pending, task => Assert.False(task.IsCompleted));

        release.TrySetResult();
        var accounts = await Task.WhenAll(pending);

        Assert.All(accounts, item => Assert.Same(unauthenticated, item));
        Assert.Equal(1, relay.AuthenticationCount);

        var device = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), cancellationToken: Token);

        Assert.NotSame(unauthenticated, device);
        Assert.Equal(SessionMode.Device, device.SessionMode);
        Assert.Equal(SessionMode.Account, unauthenticated.SessionMode);
        Assert.Equal(2, pool.Clients.Count);

        using var other = new AccountSigner();

        await Assert.ThrowsAsync<ArgumentException>(() => pool.GetAsync(relay.RelayId, other, cancellationToken: Token));

        await pool.DisposeAsync();

        Assert.Empty(pool.Clients);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.GetAsync(relay.RelayId, cancellationToken: Token));
    }

    [Fact]
    public async Task Session_expiry_is_observable_and_concurrent_renewal_runs_once()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        relay.SessionSeconds = 30;
        var client = await pool.GetAsync(relay.RelayId, account, cancellationToken: Token);
        relay.Clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(RelayAuthenticationState.Expired, client.AuthenticationState);

        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        relay.Handler = async (request, token) =>
        {
            if (request.Method == "auth.challenge")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            return request.Method == "probe" ? OfflineRelay.Json(new SequenceResult { Sequence = 4 }) : relay.Respond(request);
        };
        var pending = Enumerable.Range(0, 8).Select(_ => client.SendHttpAsync<SequenceResult>(HttpMethod.Get, "probe", cancellationToken: Token)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.All(pending, task => Assert.False(task.IsCompleted));

        release.TrySetResult();
        var responses = await Task.WhenAll(pending);

        Assert.All(responses, item => Assert.Equal(4, item.Sequence));
        Assert.Equal(2, relay.AuthenticationCount);
        Assert.All(relay.Requests.Where(item => item.Method == "probe"), item => Assert.Equal("session-2", item.Session));
    }

    [Fact]
    public async Task Unauthorized_request_is_not_replayed_and_next_request_reauthenticates()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, cancellationToken: Token);
        var calls = 0;
        relay.Handler = (request, _) => Task.FromResult(request.Method == "probe" ? Interlocked.Increment(ref calls) == 1 ? OfflineRelay.Error("unauthorized") : new HttpResponseMessage(HttpStatusCode.NoContent) : relay.Respond(request));

        Assert.Equal(
            "unauthorized",
            (await Assert.ThrowsAsync<RelayException>(() => client.SendHttpAsync(HttpMethod.Post, "probe", cancellationToken: Token))).Error.Code);
        Assert.Equal(1, calls);

        await client.SendHttpAsync(HttpMethod.Post, "probe", cancellationToken: Token);

        Assert.Equal(2, calls);
        Assert.Equal(2, relay.AuthenticationCount);
    }

    [Fact]
    public async Task Canceled_authentication_waiter_does_not_cancel_other_requests()
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        relay.Clock.Advance(TimeSpan.FromMinutes(5));
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        relay.Handler = async (request, token) =>
        {
            if (request.Method == "auth.challenge")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            return request.Method == "probe" ? new(HttpStatusCode.NoContent) : relay.Respond(request);
        };
        using var canceled = new CancellationTokenSource();
        var abandoned = client.SendHttpAsync(HttpMethod.Get, "probe", cancellationToken: canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var other = client.SendHttpAsync(HttpMethod.Get, "probe", cancellationToken: Token);
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        release.TrySetResult();
        await other.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(2, relay.AuthenticationCount);
        Assert.Single(relay.Requests, request => request.Method == "probe");
    }
}
