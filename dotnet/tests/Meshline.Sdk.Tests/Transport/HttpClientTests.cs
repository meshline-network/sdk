using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Net;
using System.Net.WebSockets;

namespace Meshline.Tests.Transport;

public sealed class HttpClientTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Peer_shutdown_cancels_a_reply_already_in_progress_and_drains_both_pumps()
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        relay.SocketHandler = async (request, socket) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            socket.Reply(request.Id!, "null");
        };
        var pending = client.SendWebSocketAsync("probe", cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var peer = Assert.Single(relay.Sockets);
        // DisposeAsync cancels the peer and closes its outgoing queue before it
        // awaits the blocked callback, making this shutdown race deterministic.
        var disposal = peer.DisposeAsync().AsTask();
        release.TrySetResult();
        var closeError = await Record.ExceptionAsync(() => disposal.WaitAsync(TimeSpan.FromSeconds(10), Token));
        var requestError = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));

        Assert.True(requestError is WebSocketException or OperationCanceledException);
        Assert.Null(closeError);
        Assert.True(peer.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Supplied_client_handles_http_and_websocket_and_remains_owned_by_caller()
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = new Meshline.Transport.RelayClientPool(relay.Options(account), relay, relay.Http);

        Assert.Empty(relay.Requests);
        Assert.Empty(relay.Upgrades);

        var client = await pool.GetAsync(relay.RelayId, account, Token);
        relay.SocketHandler = (request, socket) =>
        {
            socket.Reply(request.Id!, "{\"sequence\":7}");
            return Task.CompletedTask;
        };
        var info = await client.GetInfoAsync(Token);
        var result = await client.SendWebSocketAsync<SequenceResult>("probe", cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(relay.RelayId, info.RelayId);
        Assert.Equal(7, result.Sequence);
        Assert.Contains(relay.Requests, request => request.Method == "relay.info");
        Assert.Equal(new Uri("wss://relay.test"), Assert.Single(relay.Upgrades));

        var peer = Assert.Single(relay.Sockets);
        await pool.DisposeAsync();
        await peer.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.True(peer.ClientStreamDisposed);

        using var response = await relay.Http.GetAsync("https://relay.test/relay/info", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("accept")]
    [InlineData("upgrade")]
    public async Task Real_websocket_rejects_invalid_upgrade_before_authentication(string defect)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        relay.UpgradeHandler = (request, _) =>
        {
            if (defect == "status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var response = MemorySocket.Upgrade(request, (_, _) => throw new InvalidOperationException("An invalid handshake must not reach authentication."), out var peer);
            relay.Sockets.Enqueue(peer);
            response.Headers.Remove(defect == "accept" ? "Sec-WebSocket-Accept" : "Upgrade");
            if (defect == "accept")
                response.Headers.Add("Sec-WebSocket-Accept", "invalid");
            return Task.FromResult(response);
        };

        await Assert.ThrowsAnyAsync<WebSocketException>(() => client.SendWebSocketAsync("probe", cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(1, relay.AuthenticationCount);
        Assert.Single(relay.Upgrades);

        foreach (var peer in relay.Sockets)
        {
            await peer.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

            Assert.True(peer.ClientStreamDisposed);
            Assert.Empty(peer.Requests);
        }
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("dispose")]
    public async Task Blocked_upgrade_observes_cancellation_and_allows_cleanup(string cause)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var entered = AsyncTest.Signal();
        var exited = AsyncTest.Signal();
        relay.UpgradeHandler = async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("An interrupted handshake cannot complete.");
            }
            finally
            {
                exited.TrySetResult();
            }
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = client.SendWebSocketAsync("probe", cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (cause == "caller")
            cancellation.Cancel();
        if (cause == "timeout")
            relay.Clock.Advance(TimeSpan.FromSeconds(60));
        if (cause == "dispose")
            await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Single(relay.Upgrades);
        Assert.Empty(relay.Sockets);
    }

    [Fact]
    public async Task Abrupt_stream_loss_fails_pending_business_request_without_replay()
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var received = AsyncTest.Signal();
        relay.SocketHandler = (_, _) =>
        {
            received.TrySetResult();
            return Task.CompletedTask;
        };
        var pending = client.SendWebSocketAsync("one-shot", cancellationToken: Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var peer = Assert.Single(relay.Sockets);
        peer.Abort();

        await Assert.ThrowsAnyAsync<WebSocketException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));

        await pool.DisposeAsync();
        await peer.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Single(peer.Requests, request => request.Method == "one-shot");
        Assert.Single(relay.Upgrades);
        Assert.True(peer.ClientStreamDisposed);
    }
}
