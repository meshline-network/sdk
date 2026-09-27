using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class NotificationAvailabilityTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static readonly string[] SyncMethods = ["message.timeline.sync", "channel.read", "group.sync"];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Unconfirmed_subscription_reconnects_and_restores_the_running_managers(bool group, bool invalidResult)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var (channel, followedGroup) = await CreateFollowedResourcesAsync(fixture);
        var method = group ? "group.subscribe" : "channel.subscribe";
        var otherMethod = group ? "channel.subscribe" : "group.subscribe";
        var syncMethod = group ? "group.sync" : "channel.read";
        ClientComponent manager = group ? fixture.Client.GroupManager : fixture.Client.ChannelManager;
        ClientComponent other = group ? fixture.Client.ChannelManager : fixture.Client.GroupManager;
        var errors = new ConcurrentQueue<BackgroundErrorEventArgs>();
        fixture.Client.BackgroundError += (_, error) => errors.Enqueue(error);
        var received = AsyncTest.Signal();
        var subscriptions = 0;
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            if (request.Method == method && request.Params!.Values.Single().GetArrayLength() > 0 &&
                Interlocked.Increment(ref subscriptions) == 1)
            {
                // A successful response must be null. Otherwise leave the first
                // request unacknowledged until its normal request deadline expires.
                if (invalidResult) socket.Reply(request.Id!, "true");
                received.TrySetResult();
            }
            else socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        await other.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Any(socket => socket.Requests.Any(request => request.Method == otherMethod)));
        var old = fixture.Relay.Sockets.Single();
        await manager.StartAsync(Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (!invalidResult) fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(60));
        await AsyncTest.UntilAsync(() => old.ClientStreamDisposed && fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));
        var beforeRecovery = fixture.Relay.Requests.Count(request => request.Method == syncMethod);

        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Count == 2 &&
            HasSubscriptions(fixture.Relay.Sockets.Last(), channel, followedGroup) &&
            fixture.Relay.Requests.Count(request => request.Method == syncMethod) > beforeRecovery);
        Assert.Equal(ComponentState.Running, manager.LifecycleState);
        Assert.Equal(ComponentState.Running, other.LifecycleState);
        Assert.Contains(errors, error => invalidResult ? error.Error is InvalidDataException : error.Error is OperationCanceledException);
        Assert.All(errors, error => Assert.Equal(BackgroundOperation.Connect, error.Operation));

        await manager.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await other.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(2, Volatile.Read(ref subscriptions));
        Assert.False(fixture.Relay.Sockets.Last().ClientStreamDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_after_shared_reconnection_does_not_clear_the_replacement_socket(bool group)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var (channel, followedGroup) = await CreateFollowedResourcesAsync(fixture);
        var method = group ? "group.subscribe" : "channel.subscribe";
        var syncMethod = group ? "group.sync" : "channel.read";
        var otherMethod = group ? "channel.subscribe" : "group.subscribe";
        ClientComponent manager = group ? fixture.Client.GroupManager : fixture.Client.ChannelManager;
        ClientComponent other = group ? fixture.Client.ChannelManager : fixture.Client.GroupManager;
        await other.StartAsync(Token);
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Any(socket => HasSubscriptions(socket, channel, followedGroup)) &&
            fixture.Relay.Requests.Count(request => request.Method == syncMethod) >= 2);
        var old = fixture.Relay.Sockets.Single();
        var respond = fixture.Relay.Handler!;
        var held = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == syncMethod)
            {
                held.TrySetResult();
                // Hold completion of an HTTP operation while stop drains the
                // refresh loop, so another component can replace the socket.
                await release.Task;
            }
            return await respond(request, token);
        };
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(30));
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        var stop = manager.StopAsync(Token);
        try
        {
            await AsyncTest.UntilAsync(() => manager.LifecycleState == ComponentState.Stopping);
            old.Disconnect();
            await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
            await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Count == 2 &&
                fixture.Relay.Sockets.Last().Requests.Any(request => request.Method == otherMethod));
        }
        finally { release.TrySetResult(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);

        var replacement = fixture.Relay.Sockets.Last();
        Assert.DoesNotContain(replacement.Requests, request => request.Method == method);
        Assert.False(replacement.ClientStreamDisposed);
        Assert.Equal(ComponentState.Running, other.LifecycleState);
        await other.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(2, fixture.Relay.Sockets.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_waits_for_the_pending_subscription_before_clearing_the_shared_connection(bool group)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await CreateFollowedResourcesAsync(fixture);
        var method = group ? "group.subscribe" : "channel.subscribe";
        ClientComponent manager = group ? fixture.Client.GroupManager : fixture.Client.ChannelManager;
        ClientComponent other = group ? fixture.Client.ChannelManager : fixture.Client.GroupManager;
        var errors = new ConcurrentQueue<BackgroundErrorEventArgs>();
        fixture.Client.BackgroundError += (_, error) => errors.Enqueue(error);
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            if (request.Method != method || request.Params!.Values.Single().GetArrayLength() == 0)
                socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        await other.StartAsync(Token);
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Any(socket =>
            socket.Requests.Any(request => request.Method == "channel.subscribe") &&
            socket.Requests.Any(request => request.Method == "group.subscribe")));
        var socket = fixture.Relay.Sockets.Single();
        var pending = socket.Requests.Single(request => request.Method == method);

        var stop = manager.StopAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(5)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(stop.IsCompleted);
        Assert.Single(socket.Requests, request => request.Method == method);
        socket.Reply(pending.Id!, "null");
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        Assert.Equal(ComponentState.Running, other.LifecycleState);
        Assert.Empty(socket.Requests.Last(request => request.Method == method).Params!.Values.Single().EnumerateArray());
        Assert.False(socket.ClientStreamDisposed);
        await other.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Single(fixture.Relay.Sockets);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "pending")]
    [InlineData(false, "clear_timeout")]
    [InlineData(true, "clear_timeout")]
    [InlineData(false, "clear_rejected")]
    [InlineData(true, "clear_rejected")]
    public async Task Unconfirmed_stop_retires_the_old_connection_and_restores_only_running_components(bool group, string failure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var (channel, followedGroup) = await CreateFollowedResourcesAsync(fixture);
        var method = group ? "group.subscribe" : "channel.subscribe";
        var otherMethod = group ? "channel.subscribe" : "group.subscribe";
        var otherSync = group ? "channel.read" : "group.sync";
        ClientComponent manager = group ? fixture.Client.GroupManager : fixture.Client.ChannelManager;
        ClientComponent other = group ? fixture.Client.ChannelManager : fixture.Client.GroupManager;
        var errors = new ConcurrentQueue<BackgroundErrorEventArgs>();
        fixture.Client.BackgroundError += (_, error) => errors.Enqueue(error);
        var clearReceived = AsyncTest.Signal();
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            if (request.Method != method) socket.Reply(request.Id!, "null");
            else if (request.Params!.Values.Single().GetArrayLength() > 0)
            {
                if (failure == "clear_rejected") socket.Reply(request.Id!, "null");
            }
            else
            {
                clearReceived.TrySetResult();
                if (failure == "clear_rejected") socket.Push(new RpcFailure
                {
                    Id = request.Id,
                    Error = new() { Code = RelayError.RpcCodes["temporarily_unavailable"], Message = "Cannot clear subscriptions." }
                }.ToJson());
            }
            return Task.CompletedTask;
        };
        await other.StartAsync(Token);
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Any(socket => HasSubscriptions(socket, channel, followedGroup)));
        var old = fixture.Relay.Sockets.Single();

        var stop = manager.StopAsync(Token);
        if (failure != "clear_rejected")
        {
            await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(5)));
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(4));
            Assert.False(stop.IsCompleted);
            Assert.False(clearReceived.Task.IsCompleted);
            if (failure == "clear_timeout")
            {
                old.Reply(old.Requests.Single(request => request.Method == method).Id!, "null");
                await clearReceived.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
            // Both the old request and the final clear share this five-second budget.
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AsyncTest.UntilAsync(() => old.ClientStreamDisposed && fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        Assert.Equal(ComponentState.Running, other.LifecycleState);
        var beforeRecovery = fixture.Relay.Requests.Count(request => request.Method == otherSync);

        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Count == 2 &&
            fixture.Relay.Sockets.Last().Requests.Any(request => request.Method == otherMethod) &&
            fixture.Relay.Requests.Count(request => request.Method == otherSync) > beforeRecovery);
        var restored = fixture.Relay.Sockets.Last();
        Assert.DoesNotContain(restored.Requests, request => request.Method == method);
        Assert.NotEmpty(restored.Requests.Last(request => request.Method == otherMethod).Params!.Values.Single().EnumerateArray());
        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.Equal(BackgroundOperation.Connect, error.Operation));
        await other.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(restored.ClientStreamDisposed);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("handshake")]
    [InlineData("subscription")]
    public async Task Unavailable_notifications_allow_start_poll_stop_and_restart(string failure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await CreateFollowedResourcesAsync(fixture);
        var errors = new ConcurrentQueue<BackgroundErrorEventArgs>();
        fixture.Client.BackgroundError += (_, error) => errors.Enqueue(error);
        if (failure == "rejected")
            fixture.Relay.UpgradeHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        else if (failure == "handshake")
            fixture.Relay.UpgradeHandler = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The handshake must be canceled.");
            };
        else
            fixture.Relay.SocketHandler = (request, socket) =>
            {
                // Leave nonempty subscription requests pending, but acknowledge cleanup.
                if (request.Params!.Values.Single().GetArrayLength() == 0) socket.Reply(request.Id!, "null");
                return Task.CompletedTask;
            };
        fixture.Relay.Requests.Clear();

        await fixture.Client.StartAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AsyncTest.UntilAsync(() => SyncMethods.All(method => fixture.Relay.Requests.Any(request => request.Method == method)));
        await AsyncTest.UntilAsync(() => fixture.Relay.Upgrades.Count > 0);
        var counts = SyncMethods.ToDictionary(method => method, method => fixture.Relay.Requests.Count(request => request.Method == method));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(31));
        await AsyncTest.UntilAsync(() => SyncMethods.All(method => fixture.Relay.Requests.Count(request => request.Method == method) > counts[method]));

        await StopUnavailableClientAsync(fixture);
        Assert.Equal(ComponentState.Stopped, fixture.Client.LifecycleState);
        fixture.Relay.Requests.Clear();
        await fixture.Client.StartAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AsyncTest.UntilAsync(() => SyncMethods.All(method => fixture.Relay.Requests.Any(request => request.Method == method)));
        await StopUnavailableClientAsync(fixture);

        if (failure == "rejected") Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.Equal(BackgroundOperation.Connect, error.Operation));
    }

    [Fact]
    public async Task Reconnection_restores_subscriptions_and_notifications_without_restarting_the_client()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var (channel, group) = await CreateFollowedResourcesAsync(fixture);
        fixture.Relay.UpgradeHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var errors = new ConcurrentQueue<BackgroundErrorEventArgs>();
        fixture.Client.BackgroundError += (_, error) => errors.Enqueue(error);
        await fixture.Client.StartAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AsyncTest.UntilAsync(() => !errors.IsEmpty && fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));

        fixture.Relay.UpgradeHandler = null;
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Count == 1);
        var first = fixture.Relay.Sockets.Single();
        await AsyncTest.UntilAsync(() => HasSubscriptions(first, channel, group));
        first.Disconnect();
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Count == 2);
        var restored = fixture.Relay.Sockets.Last();
        await AsyncTest.UntilAsync(() => HasSubscriptions(restored, channel, group));

        var counts = SyncMethods.ToDictionary(method => method, method => fixture.Relay.Requests.Count(request => request.Method == method));
        restored.Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "message.timeline.changed", @params = new { head = 0 } }));
        restored.Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "channel.timeline.changed", @params = new { channel_id = channel.ChannelId, head = 0 } }));
        restored.Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "group.timeline.changed", @params = new { group_id = group.GroupId, head = 0 } }));
        await AsyncTest.UntilAsync(() => SyncMethods.All(method => fixture.Relay.Requests.Count(request => request.Method == method) > counts[method]));

        await fixture.Client.ChannelManager.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.GroupManager.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Empty(restored.Requests.Last(request => request.Method == "channel.subscribe").Params!["channel_ids"].EnumerateArray());
        Assert.Empty(restored.Requests.Last(request => request.Method == "group.subscribe").Params!["group_ids"].EnumerateArray());
        var before = fixture.Relay.Requests.Count(request => request.Method == "message.timeline.sync");
        restored.Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "message.timeline.changed", @params = new { head = 0 } }));
        await AsyncTest.UntilAsync(() => fixture.Relay.Requests.Count(request => request.Method == "message.timeline.sync") > before);
        Assert.Equal(2, fixture.Relay.Sockets.Count);

        await fixture.Client.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.All(errors, error => Assert.Equal(BackgroundOperation.Connect, error.Operation));
    }

    static bool HasSubscriptions(MemorySocket socket, ChannelRef channel, GroupRef group) =>
        socket.Requests.Any(request => request.Method == "channel.subscribe" && request.Params!["channel_ids"].EnumerateArray().Any(id => id.GetString() == channel.ChannelId))
        && socket.Requests.Any(request => request.Method == "group.subscribe" && request.Params!["group_ids"].EnumerateArray().Any(id => id.GetString() == group.GroupId));

    static async Task StopUnavailableClientAsync(TestClient fixture)
    {
        var stop = fixture.Client.StopAsync(Token);
        // The client stops its managers in sequence. Drive their bounded cleanup
        // timers, including the case where another manager reconnects meanwhile.
        while (!stop.IsCompleted)
        {
            await AsyncTest.UntilAsync(() => stop.IsCompleted || fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(5)));
            if (!stop.IsCompleted) fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    static async Task<(ChannelRef Channel, GroupRef Group)> CreateFollowedResourcesAsync(TestClient fixture)
    {
        var channels = new ChannelRelay(fixture);
        var groups = new GroupRelay(fixture);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method.StartsWith("group.", StringComparison.Ordinal)
            ? groups.Respond(request) : channels.Respond(request));
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            Assert.Contains(request.Method, new[] { "channel.subscribe", "group.subscribe" });
            socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        var channel = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
        await fixture.Client.ChannelManager.FollowAsync(channel.Ref, Token);
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new()
        {
            Name = "offline",
            MemberCapacity = 10,
            InvitePolicy = GroupInvitePolicy.Administrators
        }, Token);
        return (channel.Ref, group.Ref);
    }
}
