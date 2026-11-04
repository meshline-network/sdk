using Meshline.Components;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class BackgroundShutdownTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Shutdown_drains_group_and_channel_sync_failures_after_cancellation(bool group, bool dispose)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var channels = new ChannelRelay(fixture);
        var groups = new GroupRelay(fixture);
        HttpResponseMessage Respond(ObservedRequest request) => group ? groups.Respond(request) : channels.Respond(request);
        fixture.Relay.Handler = (request, _) => Task.FromResult(Respond(request));
        fixture.Relay.SocketHandler = (request, socket) => { socket.Reply(request.Id!, "null"); return Task.CompletedTask; };
        if (group)
            await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new() { Name = "offline", MemberCapacity = 10, InvitePolicy = GroupInvitePolicy.Administrators }, Token);
        else
        {
            var channel = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
            await fixture.Client.ChannelManager.FollowAsync(channel.Ref, Token);
        }
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == (group ? "group.sync" : "channel.read"))
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult();
                await release.Task.WaitAsync(Token);
                throw new InvalidDataException("late invalid sync response");
            }
            return Respond(request);
        };
        ClientComponent manager = group ? fixture.Client.GroupManager : fixture.Client.ChannelManager;
        await manager.StartAsync(Token);
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            shutdown = dispose ? manager.DisposeAsync().AsTask() : manager.StopAsync(Token);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(shutdown.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(dispose ? ComponentState.Disposed : ComponentState.Stopped, manager.LifecycleState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_surfaces_failed_completion_writes_and_keeps_the_pending_publication(bool group)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var channels = new ChannelRelay(fixture);
        var groups = new GroupRelay(fixture);
        HttpResponseMessage Respond(ObservedRequest request) => group ? groups.Respond(request) : channels.Respond(request);
        fixture.Relay.Handler = (request, _) => Task.FromResult(Respond(request));
        fixture.Relay.SocketHandler = (request, socket) => { socket.Reply(request.Id!, "null"); return Task.CompletedTask; };
        Func<Task> publish;
        if (group)
        {
            var created = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new() { Name = "offline", MemberCapacity = 10, InvitePolicy = GroupInvitePolicy.Administrators }, Token);
            publish = () => fixture.Client.GroupManager.CloseGroupAsync(created.Ref, Token);
        }
        else
        {
            var created = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
            publish = () => fixture.Client.ChannelManager.CloseChannelAsync(created.Ref, Token);
        }
        var method = group ? "group.close" : "channel.close";
        fixture.Relay.Handler = (request, _) => request.Method == method ? throw new HttpRequestException("offline") : Task.FromResult(Respond(request));
        await Assert.ThrowsAsync<HttpRequestException>(publish);
        var entered = AsyncTest.Signal();
        var submit = AsyncTest.Signal();
        var writing = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == method)
            {
                entered.TrySetResult();
                await submit.Task.WaitAsync(token);
            }
            return Respond(request);
        };
        var table = group ? "GroupOperations" : "ChannelOperations";
        await using var fault = await fixture.Database.CreateTriggerAsync("fail_completion",
            $"BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT, 'completion write failed'); END;");
        var writeReleased = false;
        using var observer = new DatabaseObserver((name, data) =>
        {
            if (!name.EndsWith("CommandExecuting", StringComparison.Ordinal) || data is not CommandEventData command
                || command.Command.Connection?.DataSource != fixture.Database.Options.Path
                || !command.Command.CommandText.Contains($"DELETE FROM \"{table}\"", StringComparison.Ordinal)) return;
            writing.TrySetResult();
            writeReleased = release.Task.Wait(TimeSpan.FromSeconds(10));
        });
        ClientComponent manager = group ? fixture.Client.GroupManager : fixture.Client.ChannelManager;
        var reported = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        EventHandler<BackgroundErrorEventArgs> handler = (_, args) => reported.Enqueue(args.Error);
        manager.BackgroundError += handler;
        Task? stopping = null;
        try
        {
            await manager.StartAsync(Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            submit.TrySetResult();
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            stopping = manager.StopAsync(Token);
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            var stopError = await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(10), Token));
            // Linked operation tokens may observe cancellation after the write fails.
            // The failure must reach either its background observer or the stop caller.
            if (stopError is not null) reported.Enqueue(stopError);
            Assert.NotEmpty(reported);
            Assert.All(reported, failure =>
            {
                var error = Assert.IsType<DbUpdateException>(failure);
                Assert.Contains("completion write failed", error.InnerException!.Message, StringComparison.Ordinal);
            });
            Assert.True(writeReleased);
            await using var verify = fixture.Database.Open();
            if (group) Assert.Single(await verify.GroupOperations.ToListAsync(Token));
            else Assert.Single(await verify.ChannelOperations.ToListAsync(Token));
        }
        finally
        {
            submit.TrySetResult();
            release.TrySetResult();
            if (stopping is not null) await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(10), Token));
            await fault.DisposeAsync();
            manager.BackgroundError -= handler;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_drains_a_message_sync_that_fails_after_cancellation(bool dispose)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "message.timeline.sync")
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult();
                await release.Task.WaitAsync(Token);
                throw new InvalidDataException("late invalid timeline response");
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.MessageManager;
        await manager.StartAsync(Token);
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            shutdown = dispose ? manager.DisposeAsync().AsTask() : manager.StopAsync(Token);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(shutdown.IsCompleted);
        }
        finally { release.TrySetResult(); }

        await shutdown!.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(dispose ? ComponentState.Disposed : ComponentState.Stopped, manager.LifecycleState);
        if (!dispose)
        {
            fixture.Relay.Handler = null;
            await manager.StartAsync(Token);
            await manager.StopAsync(Token);
        }
    }

    [Fact]
    public async Task Stopping_a_retry_scan_drains_a_late_read_failure_and_allows_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var database = new TestDatabase();
        using var runtime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var scans = 0;
        var coordinator = new ScanCoordinator(database.Options, async token =>
        {
            if (Interlocked.Increment(ref scans) != 1) return [];
            entered.TrySetResult();
            await release.Task.WaitAsync(Token);
            Assert.True(token.IsCancellationRequested);
            throw new SqliteException("late retry scan read failure", 5);
        });
        coordinator.Start(runtime.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await runtime.CancelAsync();
            var stopping = coordinator.StopAsync();
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        finally { release.TrySetResult(); await runtime.CancelAsync(); await coordinator.DrainAsync(); }

        using var restarted = CancellationTokenSource.CreateLinkedTokenSource(Token);
        coordinator.Start(restarted.Token);
        await AsyncTest.UntilAsync(() => Volatile.Read(ref scans) > 1);
        await restarted.CancelAsync();
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_error_observer_that_stops_message_sync_still_surfaces_its_failure(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var reported = AsyncTest.Signal();
        Exception observerError = cancellationError ? new OperationCanceledException("observer failed") : new IOException("observer failed");
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "message.timeline.sync")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                throw new InvalidDataException("invalid timeline response");
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.MessageManager;
        Task? stopping = null;
        manager.BackgroundError += (_, args) =>
        {
            if (args.Error is not InvalidDataException { Message: "invalid timeline response" }) return;
            stopping = manager.StopAsync(Token);
            reported.TrySetResult();
            throw observerError;
        };
        await manager.StartAsync(Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); }
        finally { release.TrySetResult(); }
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Same(observerError, await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
    }

    sealed class ScanCoordinator(DatabaseOptions options, Func<CancellationToken, Task<List<StatePublication>>> scan) : StateUpdateCoordinator(options)
    {
        protected override string Operation => "profile.publish";
        protected override Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token) => scan(token);
        protected override Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token) => throw new NotSupportedException();
        protected override IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent) => throw new NotSupportedException();
        protected override Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token) => throw new NotSupportedException();
        protected override void Report(string resource, Exception error) => Assert.Fail(error.ToString());
    }
}
