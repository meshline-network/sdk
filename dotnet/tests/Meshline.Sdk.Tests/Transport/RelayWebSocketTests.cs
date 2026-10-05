using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Meshline.Tests.Transport;

public sealed class RelayWebSocketTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Subscription_timeout_invalidates_readiness_before_notification_dispatch_resumes()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
        var requests = Channel.CreateUnbounded<ProtocolModel>();
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = AsyncTest.Signal();
        var respond = 0;
        relay.SocketHandler = (request, socket) =>
        {
            if (Volatile.Read(ref respond) != 0) socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        var worker = client.RunSubscriptionAsync("watch", new SequenceResult { Sequence = 0 }, requests.Reader,
            () => restored.TrySetResult(), error => failed.TrySetResult(error), Token);
        var entered = AsyncTest.Signal();
        using var release = new ManualResetEventSlim();
        client.NotificationReceived += (_, _) =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), Token)) throw new TimeoutException("The test did not release notification dispatch.");
        };
        try
        {
            requests.Writer.TryWrite(new SequenceResult { Sequence = 1 });
            await AsyncTest.UntilAsync(() => relay.Sockets.Any(socket => socket.Requests.Any(request => request.Method == "watch")));
            var old = relay.Sockets.Single();
            old.Push(new RpcRequest { Method = "message.timeline.changed" }.ToJson());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            relay.Clock.Advance(TimeSpan.FromSeconds(60));
            Assert.IsType<TimeoutException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Volatile.Write(ref respond, 1);
            var pending = client.SendWebSocketAsync("after-retirement", cancellationToken: Token);
            Assert.False(pending.IsCompleted);
            Assert.DoesNotContain(old.Requests, request => request.Method == "after-retirement");
            release.Set();

            await AsyncTest.UntilAsync(() => relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));
            relay.Clock.Advance(TimeSpan.FromSeconds(2));
            await pending.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(old.ClientStreamDisposed);
            Assert.Equal(2, relay.Sockets.Count);
            Assert.Single(relay.Sockets.Last().Requests, request => request.Method == "watch");
        }
        finally
        {
            release.Set();
            requests.Writer.TryComplete();
            await pool.DisposeAsync();
            await worker.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task Completing_a_stopped_subscription_does_not_clear_a_replacement_connection()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
        var requests = Channel.CreateUnbounded<ProtocolModel>();
        var subscribed = AsyncTest.Signal();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        relay.SocketHandler = (request, socket) =>
        {
            if (request.Method == "watch") socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        var worker = client.RunSubscriptionAsync("watch", new SequenceResult { Sequence = 0 }, requests.Reader,
            () => subscribed.TrySetResult(), errors.Enqueue, stopping.Token);
        try
        {
            requests.Writer.TryWrite(new SequenceResult { Sequence = 1 });
            await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var previous = relay.Sockets.Single();
            await stopping.CancelAsync();
            previous.Disconnect();
            await AsyncTest.UntilAsync(() => relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));
            var probe = client.SendWebSocketAsync("probe", cancellationToken: Token);
            relay.Clock.Advance(TimeSpan.FromSeconds(2));
            await AsyncTest.UntilAsync(() => relay.Sockets.Last().Requests.Any(request => request.Method == "probe"));
            var current = relay.Sockets.Last();
            Assert.NotSame(previous, current);
            Assert.False(worker.IsCompleted);

            // The producer finishes only after another consumer has reconnected.
            requests.Writer.TryComplete();
            await worker.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Empty(errors);
            Assert.True(previous.ClientStreamDisposed);
            Assert.False(current.ClientStreamDisposed);
            Assert.DoesNotContain(current.Requests, request => request.Method == "watch");
            current.Reply(current.Requests.Single(request => request.Method == "probe").Id!, "null");
            await probe.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(2, relay.Sockets.Count);
        }
        finally
        {
            requests.Writer.TryComplete();
            await pool.DisposeAsync();
            await worker.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task Completing_subscription_updates_clears_the_set_without_closing_the_shared_connection()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
        var requests = Channel.CreateUnbounded<ProtocolModel>();
        var subscribed = AsyncTest.Signal();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        relay.SocketHandler = (request, socket) =>
        {
            socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        var worker = client.RunSubscriptionAsync("watch", new SequenceResult { Sequence = 0 }, requests.Reader,
            () => subscribed.TrySetResult(), errors.Enqueue, Token);
        try
        {
            requests.Writer.TryWrite(new SequenceResult { Sequence = 1 });
            await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var socket = relay.Sockets.Single();

            requests.Writer.TryComplete();
            await worker.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(new long[] { 1, 0 }, socket.Requests.Where(request => request.Method == "watch")
                .Select(request => request.Params!["sequence"].GetInt64()));
            Assert.Empty(errors);
            await client.SendWebSocketAsync("after-clear", cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Same(socket, Assert.Single(relay.Sockets));
            Assert.False(socket.ClientStreamDisposed);
        }
        finally
        {
            requests.Writer.TryComplete();
            await pool.DisposeAsync();
            await worker.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Theory]
    [InlineData("channel.subscribe", "cancel")]
    [InlineData("channel.subscribe", "timeout")]
    [InlineData("channel.subscribe", "before_send")]
    [InlineData("group.subscribe", "cancel")]
    [InlineData("group.subscribe", "timeout")]
    [InlineData("group.subscribe", "before_send")]
    [InlineData("probe", "cancel")]
    [InlineData("probe", "timeout")]
    [InlineData("probe", "before_send")]
    public async Task Canceled_or_timed_out_rpc_calls_leave_connection_policy_to_the_caller(string method, string cancellation)
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
        relay.SocketHandler = (_, _) => Task.CompletedTask;
        await StartNotificationsAsync(client);
        var old = relay.Sockets.Single();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        if (cancellation == "before_send") await caller.CancelAsync();
        ProtocolModel request = method == "channel.subscribe" ? new ChannelSubscriptionRequest { ChannelIds = [] }
            : new GroupSubscriptionRequest { GroupIds = [] };
        var pending = client.SendWebSocketAsync(method, request, caller.Token);
        if (cancellation != "before_send")
        {
            await AsyncTest.UntilAsync(() => old.Requests.Any(request => request.Method == method));
            if (cancellation == "cancel") await caller.CancelAsync();
            else relay.Clock.Advance(TimeSpan.FromSeconds(60));
        }
        if (cancellation == "timeout")
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal("relay.websocket." + method, error.Data["operation"]);
            Assert.Equal(60d, error.Data["timeoutSeconds"]);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        }
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));

        relay.SocketHandler = (request, socket) =>
        {
            socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        if (cancellation == "before_send") Assert.DoesNotContain(old.Requests, request => request.Method == method);
        else
        {
            // The server may finish after the caller stopped waiting. A subsequent
            // request must still correlate correctly, regardless of the method name.
            old.Reply(old.Requests.Single(request => request.Method == method).Id!, "null");
        }
        await client.SendWebSocketAsync("after-cancel", cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Same(old, Assert.Single(relay.Sockets));
        Assert.False(old.ClientStreamDisposed);
        Assert.Equal(cancellation == "before_send" ? 0 : 1, old.Requests.Count(request => request.Method == method));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Socket_request_bounds_readiness_wait_by_deadline_and_caller_cancellation(bool typed, bool cancel)
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        relay.UpgradeHandler = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The handshake must be canceled.");
        };
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task request = typed ? client.SendWebSocketAsync<SequenceResult>("probe", cancellationToken: caller.Token)
            : client.SendWebSocketAsync("probe", cancellationToken: caller.Token);
        await AsyncTest.UntilAsync(() => relay.Upgrades.Count > 0);

        if (cancel) await caller.CancelAsync();
        else relay.Clock.Advance(TimeSpan.FromSeconds(60));

        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), Token));
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal("relay.websocket.probe", error.Data["operation"]);
        }
        Assert.Empty(relay.Sockets);
    }

    [Fact]
    public async Task Socket_correlates_out_of_order_fragmented_results_and_dispatches_notifications()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), cancellationToken: Token);
        relay.SocketHandler = (_, _) => Task.CompletedTask;
        await StartNotificationsAsync(client).WaitAsync(TimeSpan.FromSeconds(5), Token);
        var notice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, item) => notice.TrySetResult(item.Method);
        var first = client.SendWebSocketAsync<SequenceResult>("first", cancellationToken: Token);
        await AsyncTest.UntilAsync(() => first.IsCompleted || relay.Sockets.TryPeek(out var value) && value.Requests.Any(r => r.Method == "first"));
        if (first.IsCompleted)
            await first;
        var second = client.SendWebSocketAsync<SequenceResult>("second", cancellationToken: Token);
        var socket = relay.Sockets.Single();
        await AsyncTest.UntilAsync(() => socket.Requests.Any(r => r.Method == "second"));
        socket.Reply(socket.Requests.Single(r => r.Method == "second").Id!, "{\"sequence\":2}");
        var id = socket.Requests.Single(r => r.Method == "first").Id;
        socket.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{\"sequence\":1}}", 3);
        socket.Push("{\"jsonrpc\":\"2.0\",\"method\":\"message.timeline.changed\"}", 5);

        Assert.Equal(1, (await first.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: Token)).Sequence);
        Assert.Equal(2, (await second.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: Token)).Sequence);
        Assert.Equal("message.timeline.changed", await notice.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: Token));
    }

    [Fact]
    public async Task Socket_disconnect_reconnects_notifications_without_replaying_business_request()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), cancellationToken: Token);
        relay.SocketHandler = (_, _) => Task.CompletedTask;
        await StartNotificationsAsync(client).WaitAsync(TimeSpan.FromSeconds(5), Token);
        var request = client.SendWebSocketAsync("one-shot", cancellationToken: Token);
        await AsyncTest.UntilAsync(() => request.IsCompleted || relay.Sockets.TryPeek(out var value) && value.Requests.Any(r => r.Method == "one-shot"));
        if (request.IsCompleted)
            await request;
        relay.Sockets.Single().Disconnect();

        await Assert.ThrowsAnyAsync<WebSocketException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: Token));

        await AsyncTest.UntilAsync(() => relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));
        // Renewal is cancelled on disconnect; the remaining timer is reconnect backoff.
        relay.Clock.Advance(TimeSpan.FromSeconds(31));
        await AsyncTest.UntilAsync(() => relay.Sockets.Count == 2);

        Assert.Single(relay.Sockets.SelectMany(s => s.Requests), r => r.Method == "one-shot");

        await pool.DisposeAsync();
        await Task.WhenAll(relay.Sockets.Select(socket => socket.Completion)).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.All(relay.Sockets, socket => Assert.True(socket.ClientStreamDisposed));
    }

    [Fact]
    public async Task Socket_renewal_uses_virtual_clock_and_canceled_calls_ignore_late_replies()
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        relay.SessionSeconds = 30;
        relay.SocketHandler = (_, _) => Task.CompletedTask;
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
        await StartNotificationsAsync(client);
        var socket = relay.Sockets.Single();
        await AsyncTest.UntilAsync(() => relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(30)));
        var initial = relay.AuthenticationCount;
        relay.Clock.Advance(TimeSpan.FromSeconds(25));
        await AsyncTest.UntilAsync(() => relay.AuthenticationCount > initial);
        using var canceled = new CancellationTokenSource();
        var pending = client.SendWebSocketAsync("cancel-me", cancellationToken: canceled.Token);
        await AsyncTest.UntilAsync(() => socket.Requests.Any(request => request.Method == "cancel-me"));
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));

        socket.Reply(socket.Requests.Single(request => request.Method == "cancel-me").Id!, "null");
        // A fresh correlated response proves the receive loop survived the late response.
        relay.SocketHandler = (request, active) =>
        {
            active.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        await client.SendWebSocketAsync("after-cancel", cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Single(relay.Sockets);
    }

    [Fact]
    public async Task Binary_frame_fails_pending_call_and_disposal_drains_socket()
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        relay.SocketHandler = (_, _) => Task.CompletedTask;
        var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
        await StartNotificationsAsync(client);
        var socket = relay.Sockets.Single();
        var pending = client.SendWebSocketAsync("probe", cancellationToken: Token);
        await AsyncTest.UntilAsync(() => socket.Requests.Any(request => request.Method == "probe"));
        socket.Binary();

        await Assert.ThrowsAsync<InvalidDataException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));

        await pool.DisposeAsync();
        await socket.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.True(socket.ClientStreamDisposed);
    }

    static async Task StartNotificationsAsync(RelayClient client)
    {
        var connected = AsyncTest.Signal();
        void OnConnected(object? sender, EventArgs args) => connected.TrySetResult();
        client.SocketConnected += OnConnected;
        try
        {
            client.StartNotifications();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        finally { client.SocketConnected -= OnConnected; }
    }
}
