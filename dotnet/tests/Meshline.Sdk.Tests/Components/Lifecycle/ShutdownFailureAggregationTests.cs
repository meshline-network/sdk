using Meshline.Components;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class ShutdownFailureAggregationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cache_observer_failure_reaches_stop(bool profileChanged, bool cancellationError)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var previous = manager.Profile;
        if (profileChanged)
        {
            var result = fixture.Relay.Profiles[fixture.Account.AccountId];
            var profile = result.Profile with { Nickname = "remote change", UpdatedAt = result.Profile.UpdatedAt + 1, DeviceSignature = [] };
            profile = profile with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(profile.GetSigningInput(TestNetwork.Context), Token)] };
            fixture.Relay.Profiles[fixture.Account.AccountId] = result with { Profile = profile };
        }
        else
            fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
                ? throw new TimeoutException("profile offline") : Task.FromResult(fixture.Relay.Respond(request));
        var entered = AsyncTest.Signal();
        Exception observerError = cancellationError ? new OperationCanceledException("profile cache observer failed") : new IOException("profile cache observer failed");
        Task? stopping = null;
        void Observe()
        {
            stopping = manager.StopAsync(Token);
            entered.TrySetResult();
            throw observerError;
        }
        if (profileChanged) manager.ProfileChanged += (_, _) => Observe();
        else manager.BackgroundError += (_, args) => { if (args.Error is TimeoutException { Message: "profile offline" }) Observe(); };
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(previous, await manager.GetProfileAsync(cancellationToken: Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var error = await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(error);
        var failures = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [error];
        Assert.Single(failures, failure => ReferenceEquals(observerError, failure));
        Assert.Equal(profileChanged ? 1 : 2, failures.Length);
        fixture.Relay.Handler = null;
        await manager.StopAsync(Token);
        await manager.StartAsync(Token);
        await manager.StopAsync(Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_refresh_preserves_only_required_failures_for_shutdown(bool observerFails)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        var cache = new MetadataCache<string>("cached");
        var observerError = new IOException("cache observer failed");
        var requestError = new TimeoutException("offline");
        Task<string?> Fetch(CancellationToken _) => Task.FromException<string?>(requestError);
        void Report(Exception _) { if (observerFails) BackgroundFailure.Notify(() => throw observerError); }
        Assert.Equal("cached", await cache.ReadAsync(_ => true, Fetch, Report, Token));
        var readError = await Record.ExceptionAsync(() => cache.ReadAsync(_ => true, Fetch, Report, Token, force: true));
        Assert.NotNull(readError);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("updated", await cache.ReadAsync(_ => true, _ => Task.FromResult<string?>("updated"), Report, Token, force: true));
        var stopError = await Record.ExceptionAsync(() => cache.StopAsync());
        if (observerFails)
        {
            var failures = Assert.IsType<AggregateException>(stopError).Flatten().InnerExceptions;
            Assert.Equal(2, failures.Count);
            Assert.Contains(observerError, failures);
            Assert.Contains(requestError, failures);
        }
        else Assert.Null(stopError);
        await cache.StopAsync();
        Assert.Equal("restarted", await cache.ReadAsync(_ => true, _ => Task.FromResult<string?>("restarted"), Report, Token, force: true));
        await cache.StopAsync(dispose: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Subscription_failure_and_cleanup_failure_both_reach_stop(bool group, bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        ClientComponent manager;
        if (group)
        {
            var host = new GroupRelay(fixture);
            host.Install();
            await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId,
                new() { Name = "group", MemberCapacity = 10, InvitePolicy = GroupInvitePolicy.Administrators }, Token);
            manager = fixture.Client.GroupManager;
        }
        else
        {
            var host = new ChannelRelay(fixture);
            host.Install();
            var channel = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
            await fixture.Client.ChannelManager.FollowAsync(channel.Ref, Token);
            manager = fixture.Client.ChannelManager;
        }
        var idsField = group ? "group_ids" : "channel_ids";
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var notified = AsyncTest.Signal();
        fixture.Relay.SocketHandler = async (request, socket) =>
        {
            var subscribing = request.Params![idsField].GetArrayLength() > 0;
            if (subscribing)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Token);
            }
            socket.Push(new RpcFailure
            {
                Id = request.Id,
                Error = new() { Code = RelayError.RpcCodes["temporarily_unavailable"], Message = subscribing ? "subscribe failed" : "clear failed" }
            }.ToJson());
        };
        Exception firstError = cancellationError ? new OperationCanceledException("first observer failed") : new IOException("first observer failed");
        var cleanupError = new InvalidOperationException("cleanup observer failed");
        Task? stopping = null;
        manager.BackgroundError += (_, args) =>
        {
            if (args.Error is RelayException { Error.Message: "subscribe failed" })
            {
                stopping = manager.StopAsync(Token);
                notified.TrySetResult();
                throw firstError;
            }
            if (args.Error is RelayException { Error.Message: "clear failed" }) throw cleanupError;
        };
        await manager.StartAsync(Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        finally { release.TrySetResult(); }
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var error = await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(error);
        var failures = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [error];
        Assert.Contains(firstError, failures);
        Assert.Contains(cleanupError, failures);
        Assert.Equal(2, failures.Length);
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        await manager.StopAsync(Token);
        fixture.Relay.SocketHandler = (request, socket) => { socket.Reply(request.Id!, "null"); return Task.CompletedTask; };
        await manager.StartAsync(Token);
        await manager.StopAsync(Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_message_worker_failures_both_reach_stop(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var requests = AsyncTest.Signal();
        var releaseRequests = AsyncTest.Signal();
        var observers = AsyncTest.Signal();
        var releaseObservers = AsyncTest.Signal();
        var requestCount = 0;
        var observerCount = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method is "message.send" or "message.timeline.sync")
            {
                if (Interlocked.Increment(ref requestCount) == 2) requests.TrySetResult();
                await releaseRequests.Task.WaitAsync(token);
                throw new InvalidDataException(request.Method == "message.send" ? "send failed" : "sync failed");
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.MessageManager;
        var sendError = new IOException("send observer failed");
        Exception syncError = cancellationError ? new OperationCanceledException("sync observer failed") : new IOException("sync observer failed");
        manager.BackgroundError += (_, args) =>
        {
            if (args.Error is not InvalidDataException { Message: "send failed" or "sync failed" }) return;
            if (Interlocked.Increment(ref observerCount) == 2) observers.TrySetResult();
            Assert.True(releaseObservers.Task.Wait(TimeSpan.FromSeconds(10)));
            throw args.Error.Message == "send failed" ? sendError : syncError;
        };
        await manager.StartAsync(Token);
        Task? stopping = null;
        try
        {
            await requests.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            releaseRequests.TrySetResult();
            await observers.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            stopping = manager.StopAsync(Token);
        }
        finally { releaseRequests.TrySetResult(); releaseObservers.TrySetResult(); }
        var error = await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(error);
        var failures = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [error];
        Assert.Contains(sendError, failures);
        Assert.Contains(syncError, failures);
        Assert.Equal(2, failures.Length);
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        await manager.StopAsync(Token);
        fixture.Relay.Handler = null;
        await manager.StartAsync(Token);
        await manager.StopAsync(Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notification_error_observer_failure_reaches_disposal(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var relay = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var notified = AsyncTest.Signal();
        Task? disposal = null;
        Exception observerError = cancellationError ? new OperationCanceledException("notification observer failed") : new IOException("notification observer failed");
        relay.ErrorOccurred += (_, error) =>
        {
            if (error is not InvalidDataException) return;
            disposal = relay.DisposeAsync().AsTask();
            notified.TrySetResult();
            throw observerError;
        };
        relay.StartNotifications();
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Any(socket => socket.AuthenticationReplied.IsCompletedSuccessfully));
        fixture.Relay.Sockets.Single().Binary();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Same(observerError, await Record.ExceptionAsync(() => disposal!.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        Assert.True(fixture.Relay.Sockets.Single().ClientStreamDisposed);
        await relay.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_notification_observer_can_dispose_without_losing_its_failure(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var relay = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var notified = AsyncTest.Signal();
        Task? disposal = null;
        Exception observerError = cancellationError ? new OperationCanceledException("notification observer failed") : new IOException("notification observer failed");
        relay.NotificationReceived += (_, _) =>
        {
            disposal = relay.DisposeAsync().AsTask();
            notified.TrySetResult();
            throw observerError;
        };
        relay.StartNotifications();
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Any(socket => socket.AuthenticationReplied.IsCompletedSuccessfully));
        fixture.Relay.Sockets.Single().Push(new RpcRequest { Method = "test.changed" }.ToJson());
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Same(observerError, await Record.ExceptionAsync(() => disposal!.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        Assert.True(fixture.Relay.Sockets.Single().ClientStreamDisposed);
    }

    [Fact]
    public async Task Disposal_keeps_notification_and_final_observer_failures()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var relay = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var notified = AsyncTest.Signal();
        Task? disposal = null;
        var firstError = new OperationCanceledException("initial observer failed");
        var cleanupError = new IOException("final error observer failed");
        var stateError = new InvalidOperationException("final state observer failed");
        relay.ErrorOccurred += (_, error) =>
        {
            if (error is InvalidDataException)
            {
                disposal = relay.DisposeAsync().AsTask();
                notified.TrySetResult();
                throw firstError;
            }
            if (error is InvalidOperationException { Message: "The relay connection was disposed or invalidated." }) throw cleanupError;
        };
        relay.StateChanged += (_, _) =>
        {
            if (disposal is not null && relay.AuthenticationState == RelayAuthenticationState.None) throw stateError;
        };
        relay.StartNotifications();
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Any(socket => socket.AuthenticationReplied.IsCompletedSuccessfully));
        fixture.Relay.Sockets.Single().Binary();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var error = await Assert.ThrowsAsync<AggregateException>(() => disposal!.WaitAsync(TimeSpan.FromSeconds(10), Token));
        var failures = error.Flatten().InnerExceptions;
        Assert.Equal(3, failures.Count);
        Assert.Contains(firstError, failures);
        Assert.Contains(cleanupError, failures);
        Assert.Contains(stateError, failures);
        Assert.True(fixture.Relay.Sockets.Single().ClientStreamDisposed);
        await relay.DisposeAsync();
    }
}
