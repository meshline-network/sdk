using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Threading.Channels;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class BackgroundObserverTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_message_observer_that_requests_stop_still_surfaces_its_failure(bool contactRequest, bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var sender = new TestClient();
        await sender.InitializeAsync();
        await using var receiver = new TestClient();
        await receiver.InitializeAsync();
        sender.Relay.Devices[receiver.Account.AccountId] = receiver.Client.DeviceState!;
        var outgoing = await sender.Client.MessageManager.AddContactAsync(receiver.Account.AccountId, cancellationToken: Token);
        MessageSendRequest message;
        await using (var database = sender.Database.Open())
            message = ProtocolModel.FromJson<MessageSendRequest>((await database.MessageOutbox.SingleAsync(row => row.MessageId == outgoing.MessageId, Token)).RequestJson)!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var notified = AsyncTest.Signal();
        receiver.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "message.timeline.sync")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfflineRelay.Json(new MessageTimelinePage
                {
                    Items = long.Parse(RequestQuery.Parse(request)["after"]) < 0 ? [new MessageTimelineEntry
                    {
                        Sequence = 0, Envelope = message.Envelope, KeyBox = message.RecipientBoxes[0],
                        AcceptedAt = Clock.UtcNow.ToUnixTimeSeconds()
                    }] : [],
                    Certificates = [sender.Client.Device!],
                    HasMore = false
                });
            }
            return receiver.Relay.Respond(request);
        };
        var manager = receiver.Client.MessageManager;
        Exception observerError = cancellationError ? new OperationCanceledException("message observer failed") : new IOException("message observer failed");
        Task? stopping = null;
        var continued = false;
        void OnNotification()
        {
            stopping = manager.StopAsync(Token);
            notified.TrySetResult();
            throw observerError;
        }
        await manager.StartAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (contactRequest)
            {
                manager.ContactRequestChanged += (_, _) => OnNotification();
                manager.ContactRequestChanged += (_, _) => continued = true;
            }
            else
            {
                manager.TimelineChanged += (_, _) => OnNotification();
                manager.TimelineChanged += (_, _) => continued = true;
            }
        }
        finally { release.TrySetResult(); }
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Same(observerError, await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        Assert.False(continued);
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        await manager.StopAsync(Token);
    }

    [Fact]
    public async Task A_failed_retry_worker_is_reported_once_and_restart_resumes_pending_work()
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));
        var pending = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, pending.Status);

        var reported = AsyncTest.Signal();
        var observerError = new IOException("profile retry observer failed");
        manager.BackgroundError += (_, _) => { reported.TrySetResult(); throw observerError; };
        var injected = 0;
        using var observer = new DatabaseObserver((name, data) =>
        {
            if (name.EndsWith("CommandExecuting", StringComparison.Ordinal) && data is CommandEventData command
                && command.Command.Connection?.DataSource == fixture.Database.Options.Path
                && command.Command.CommandText.Contains("FROM \"SignedRequests\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref injected, 1) == 0)
                throw new SqliteException("retry scan failed", 5);
        });
        await manager.StartAsync(Token);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Same(observerError, await Record.ExceptionAsync(() => manager.StopAsync(Token)));
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        await manager.StopAsync(Token);
        var stopped = await manager.GetUpdateStateAsync(Token);
        Assert.NotNull(stopped.PendingUpdate);
        Assert.Equal(pending.PendingUpdate!.Nickname, stopped.PendingUpdate.Nickname);
        Assert.Equal(pending.PendingUpdate.Bio, stopped.PendingUpdate.Bio);
        Assert.Equal(pending.PendingUpdate.Avatar, stopped.PendingUpdate.Avatar);
        Assert.Equal(pending.PendingUpdate.PublicDiscovery, stopped.PendingUpdate.PublicDiscovery);
        Assert.Equal(pending.NextAttemptAt, stopped.NextAttemptAt);

        fixture.Relay.Handler = null;
        clock.Advance(TimeSpan.FromSeconds(1));
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await manager.GetUpdateStateAsync(Token)).Status == ProfileUpdateStatus.Synchronized);
        Assert.Equal("latest", manager.Profile!.Nickname);
        await manager.StopAsync(Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stopping_drains_retry_error_observers_and_restart_resumes_pending_work(bool holdObserver, bool cancellationError)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "pending" }, Token));
        var retryEntered = AsyncTest.Signal();
        var retryRelease = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish")
            {
                retryEntered.TrySetResult();
                await retryRelease.Task.WaitAsync(token);
                throw new TimeoutException("offline retry");
            }
            return fixture.Relay.Respond(request);
        };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var exited = AsyncTest.Signal();
        Exception observerError = cancellationError ? new OperationCanceledException("retry observer failed") : new IOException("retry observer failed");
        Task? stopping = null;
        EventHandler<BackgroundErrorEventArgs> handler = (_, _) =>
        {
            try
            {
                if (!holdObserver) stopping = manager.StopAsync(Token);
                entered.TrySetResult();
                if (holdObserver) Assert.True(release.Task.Wait(TimeSpan.FromSeconds(10)));
                throw observerError;
            }
            finally { exited.TrySetResult(); }
        };
        manager.BackgroundError += handler;
        clock.Advance(TimeSpan.FromSeconds(1));
        await manager.StartAsync(Token);
        try
        {
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            retryRelease.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (holdObserver)
            {
                stopping = manager.StopAsync(Token);
                await Task.WhenAny(stopping, Task.Delay(100, Token));
                Assert.False(stopping.IsCompleted);
            }
        }
        finally { retryRelease.TrySetResult(); release.TrySetResult(); }
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Same(observerError, await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        await manager.StopAsync(Token);
        var pending = await manager.GetUpdateStateAsync(Token);
        Assert.Equal("pending", pending.PendingUpdate!.Nickname.Value);
        Assert.Equal("offline retry", pending.LastError!.Message);

        manager.BackgroundError -= handler;
        fixture.Relay.Handler = null;
        clock.Advance(TimeSpan.FromSeconds(2));
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await manager.GetUpdateStateAsync(Token)).Status == ProfileUpdateStatus.Synchronized);
        Assert.Equal("pending", manager.Profile!.Nickname);
        await manager.StopAsync(Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_subscription_error_observer_failure_survives_stop_and_subscription_cleanup(bool group, bool cancellationError)
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
        var method = group ? "group.subscribe" : "channel.subscribe";
        var idsField = group ? "group_ids" : "channel_ids";
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var notified = AsyncTest.Signal();
        var cleared = AsyncTest.Signal();
        fixture.Relay.SocketHandler = async (request, socket) =>
        {
            Assert.Equal(method, request.Method);
            if (request.Params![idsField].GetArrayLength() > 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Token);
                socket.Push(new RpcFailure
                {
                    Id = request.Id,
                    Error = new() { Code = RelayError.RpcCodes["temporarily_unavailable"], Message = "subscription failed" }
                }.ToJson());
            }
            else
            {
                socket.Reply(request.Id!, "null");
                cleared.TrySetResult();
            }
        };
        Exception observerError = cancellationError ? new OperationCanceledException("subscription observer failed") : new IOException("subscription observer failed");
        Task? stopping = null;
        manager.BackgroundError += (_, args) =>
        {
            if (args.Error is not RelayException { Error.Message: "subscription failed" }) return;
            stopping = manager.StopAsync(Token);
            notified.TrySetResult();
            throw observerError;
        };
        await manager.StartAsync(Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        finally { release.TrySetResult(); }
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Same(observerError, await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        Assert.True(cleared.Task.IsCompletedSuccessfully);
        Assert.False(Assert.Single(fixture.Relay.Sockets).ClientStreamDisposed);
        await manager.StopAsync(Token);
    }

    [Fact]
    public async Task A_subscription_worker_preserves_marked_observer_cancellation_after_runtime_cancellation()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var cleared = AsyncTest.Signal();
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            if (request.Params!["channel_ids"].GetArrayLength() > 0)
                socket.Push(new RpcFailure
                {
                    Id = request.Id,
                    Error = new() { Code = RelayError.RpcCodes["temporarily_unavailable"], Message = "subscription failed" }
                }.ToJson());
            else
            {
                socket.Reply(request.Id!, "null");
                cleared.TrySetResult();
            }
            return Task.CompletedTask;
        };
        var relay = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        using var runtime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var requests = Channel.CreateUnbounded<ProtocolModel>();
        var observerError = new OperationCanceledException("subscription observer failed");
        var worker = relay.RunSubscriptionAsync("channel.subscribe", new ChannelSubscriptionRequest { ChannelIds = [] }, requests.Reader, () => { }, _ =>
        {
            runtime.Cancel();
            requests.Writer.TryComplete();
            BackgroundFailure.Notify(() => throw observerError);
        }, runtime.Token);
        requests.Writer.TryWrite(new ChannelSubscriptionRequest { ChannelIds = [Identifiers.CreateMessageId()] });
        try
        {
            Assert.Same(observerError, await Record.ExceptionAsync(() => worker.WaitAsync(TimeSpan.FromSeconds(10), Token)));
            Assert.True(cleared.Task.IsCompletedSuccessfully);
        }
        finally { runtime.Cancel(); requests.Writer.TryComplete(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_relay_observer_failure_propagates_through_a_stopping_background_request(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var connection = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var connected = AsyncTest.Signal();
        connection.SocketConnected += (_, _) => connected.TrySetResult();
        connection.StartNotifications();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var notified = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "message.timeline.sync")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                throw new HttpRequestException("timeline offline");
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.MessageManager;
        Exception observerError = cancellationError ? new OperationCanceledException("relay observer failed") : new IOException("relay observer failed");
        Task? stopping = null;
        var observed = 0;
        EventHandler<RelayClient> handler = (_, relay) =>
        {
            if (relay.LastError is not HttpRequestException { Message: "timeline offline" }
                || Interlocked.CompareExchange(ref observed, 1, 0) != 0) return;
            stopping = manager.StopAsync(Token);
            notified.TrySetResult();
            throw observerError;
        };
        await manager.StartAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            // Complete the initial send before injecting a timeline failure, so
            // another connection-state transition cannot invoke this observer first.
            await AsyncTest.UntilAsync(async () =>
            {
                await using var database = fixture.Database.Open();
                return !await database.MessageOutbox.AnyAsync(row => row.State != MessageSendState.TargetAccepted, Token);
            });
            fixture.Pool.RelayChanged += handler;
            release.TrySetResult();
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Same(observerError, await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token)));
            Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        }
        finally
        {
            fixture.Pool.RelayChanged -= handler;
            release.TrySetResult();
        }
    }
}
