using Meshline.Components;
using Meshline.Models.Client;
using Microsoft.EntityFrameworkCore;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class DisposalCleanupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Device_disposal_retires_sessions_even_when_an_abandoned_publication_reports_an_error(bool observerFails, bool abandon, bool ownerDisposal)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.DeviceManager;
        var relay = await fixture.Pool.GetAsync(fixture.Relay.RelayId, manager, Token);
        Assert.Equal(SessionMode.Device, relay.SessionMode);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var observerError = new IOException("abandoned device observer failed");
        if (observerFails)
            manager.DeviceStateChanged += (_, _) => { if (abandon) caller.Cancel(); throw observerError; };
        var publication = manager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: caller.Token);
        if (abandon) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication.WaitAsync(TimeSpan.FromSeconds(10), Token));
        else if (observerFails) Assert.Same(observerError, await Record.ExceptionAsync(() => publication.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        else await publication.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var failure = await Record.ExceptionAsync(() => (ownerDisposal ? fixture.Client.DisposeAsync() : manager.DisposeAsync()).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
        if (abandon) Assert.Same(observerError, failure);
        else Assert.Null(failure);
        Assert.DoesNotContain(fixture.Pool.Clients, client => client.SessionMode == SessionMode.Device);
        Assert.Equal(Meshline.Transport.RelayConnectionState.Disconnected, relay.ConnectionState);
        await (ownerDisposal ? fixture.Client.DisposeAsync() : manager.DisposeAsync());
        Assert.Equal(ComponentState.Disposed, manager.LifecycleState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_disposal_reports_cache_failures_even_when_an_abandoned_publication_also_failed(bool abandon)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var cacheError = new IOException("cache observer failed");
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw new TimeoutException("lookup offline") : Task.FromResult(fixture.Relay.Respond(request));
        manager.BackgroundError += (_, _) => throw cacheError;
        Assert.NotNull(await Record.ExceptionAsync(() => manager.RefreshProfileAsync(cancellationToken: Token)));
        fixture.Relay.Handler = null;
        var publicationError = new IOException("abandoned profile observer failed");
        if (abandon)
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
            manager.ProfileChanged += (_, _) => { caller.Cancel(); throw publicationError; };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.UpdateProfileAsync(new() { Nickname = "confirmed" }, caller.Token));
        }
        var failure = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(failure);
        var errors = Flatten(failure);
        if (abandon) Assert.Contains(publicationError, errors);
        Assert.Contains(cacheError, errors);
        await manager.DisposeAsync();
        Assert.Equal(ComponentState.Disposed, manager.LifecycleState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Account_disposal_drains_route_cache_after_publication_failure_and_is_retryable(bool abandon)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        var cacheError = new IOException("route error observer failed");
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.resolve"
            ? throw new TimeoutException("route offline") : Task.FromResult(fixture.Relay.Respond(request));
        manager.BackgroundError += (_, _) => throw cacheError;
        Assert.NotNull(await Record.ExceptionAsync(() => manager.RefreshRouteAsync(cancellationToken: Token)));
        fixture.Relay.Handler = null;
        var publicationError = new IOException("abandoned route observer failed");
        if (abandon)
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
            manager.AccountChanged += (_, _) => { caller.Cancel(); throw publicationError; };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), cancellationToken: caller.Token));
        }
        var failure = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(failure);
        var errors = Flatten(failure);
        if (abandon) Assert.Contains(publicationError, errors);
        Assert.Contains(cacheError, errors);
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        Assert.Equal(ComponentState.Disposed, manager.LifecycleState);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Message_disposal_retries_pending_route_persistence_after_a_recorder_failure(bool repairBeforeDisposal, bool running)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var nextRelay = new OfflineRelay("next.test");
        fixture.Relay.LinkTo(nextRelay);
        nextRelay.Devices[fixture.Account.AccountId] = fixture.Client.DeviceManager.DeviceState!;
        var manager = fixture.Client.MessageManager;
        await manager.StopAsync(Token);
        Assert.Equal(ResourceSyncState.CaughtUp, (await manager.SynchronizeAsync(fixture.Relay.RelayId, Token)).State);
        if (running) await manager.StartAsync(Token);
        await using var fault = await fixture.Database.CreateTriggerAsync("fail_timeline",
            "BEFORE INSERT ON AccountTimelines BEGIN SELECT RAISE(ABORT, 'timeline write failed'); END;");
        var reset = false;
        var disposedEvents = 0;
        manager.StateChanged += (_, change) => { if (change.CurrentState == ComponentState.Disposed) disposedEvents++; };
        manager.SyncStatusChanged += (_, status) => { if (status.State == ResourceSyncState.Idle) reset = true; };
        var observerError = new IOException("route recorder observer failed");
        var reported = AsyncTest.Signal();
        EventHandler<BackgroundErrorEventArgs> handler = (_, _) => { reported.TrySetResult(); throw observerError; };
        manager.BackgroundError += handler;
        var completed = false;
        try
        {
            await fixture.Client.AccountManager.PublishRouteAsync(nextRelay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            manager.BackgroundError -= handler;
            if (repairBeforeDisposal) await fault.DisposeAsync();
            var failure = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.NotNull(failure);
            var errors = Flatten(failure);
            Assert.Contains(observerError, errors);
            Assert.True(reset);
            if (!repairBeforeDisposal)
            {
                Assert.Contains(errors, error => error is DbUpdateException { InnerException.Message: var message } && message.Contains("timeline write failed", StringComparison.Ordinal));
                await fault.DisposeAsync();
            }
            else
            {
                await using var read = fixture.Database.Open();
                Assert.True(await read.AccountTimelines.AnyAsync(row => row.RelayId == nextRelay.RelayId, Token));
            }
            await manager.DisposeAsync();
            Assert.Equal(ComponentState.Disposed, manager.LifecycleState);
            Assert.Equal(1, disposedEvents);
            await using var verify = fixture.Database.Open();
            Assert.True(await verify.AccountTimelines.AnyAsync(row => row.RelayId == nextRelay.RelayId, Token));
            completed = true;
        }
        finally
        {
            manager.BackgroundError -= handler;
            await fault.DisposeAsync();
            if (!completed) _ = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Resource_disposal_resets_sync_and_drains_cache_despite_publication_and_status_observer_failures(bool channel, bool failStatus)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var publicationError = new IOException("abandoned publication observer failed");
        var statusError = new IOException("idle status observer failed");
        var reportError = new IOException("status error observer failed");
        var cacheError = new IOException("descriptor cache observer failed");
        var reset = false;
        ClientComponent component;
        Task publication;
        void OnStatus(object? sender, ResourceSyncStatus status)
        {
            if (status.State != ResourceSyncState.Idle) return;
            reset = true;
            if (failStatus) throw statusError;
        }
        void OnError(object? sender, BackgroundErrorEventArgs args)
        {
            if (ReferenceEquals(args.Error, statusError)) throw reportError;
            if (args.Error is TimeoutException) throw cacheError;
        }
        if (channel)
        {
            var host = new ChannelRelay(fixture);
            host.Install();
            var manager = fixture.Client.ChannelManager;
            component = manager;
            var created = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
            Assert.Equal(ResourceSyncState.CaughtUp, (await manager.SynchronizeAsync(created.Ref, Token)).State);
            manager.BackgroundError += OnError;
            fixture.Relay.Handler = (request, _) => request.Method == "channel.resolve"
                ? throw new TimeoutException("descriptor offline") : Task.FromResult(host.Respond(request));
            Assert.NotNull(await Record.ExceptionAsync(() => manager.RefreshChannelAsync(created.Ref, Token)));
            host.Install();
            manager.SyncStatusChanged += OnStatus;
            manager.ChannelChanged += (_, _) => { caller.Cancel(); throw publicationError; };
            publication = manager.UpdateChannelAsync(created.Ref, new() { Name = "confirmed" }, caller.Token);
        }
        else
        {
            var host = new GroupRelay(fixture);
            host.Install();
            var manager = fixture.Client.GroupManager;
            component = manager;
            var created = await manager.CreateGroupAsync(fixture.Relay.RelayId, new()
            {
                Name = "group",
                MemberCapacity = 10,
                InvitePolicy = GroupInvitePolicy.Administrators
            }, Token);
            Assert.Equal(ResourceSyncState.CaughtUp, (await manager.SynchronizeAsync(created.Ref, Token)).State);
            manager.BackgroundError += OnError;
            manager.SyncStatusChanged += OnStatus;
            manager.GroupChanged += (_, _) => { caller.Cancel(); throw publicationError; };
            publication = manager.UpdateGroupAsync(created.Ref, new() { Name = "confirmed" }, caller.Token);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication.WaitAsync(TimeSpan.FromSeconds(10), Token));
        var failure = await Record.ExceptionAsync(() => component.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(failure);
        var errors = Flatten(failure);
        Assert.Contains(publicationError, errors);
        if (channel) Assert.Contains(cacheError, errors);
        if (failStatus) Assert.Contains(reportError, errors);
        Assert.True(reset);
        await component.DisposeAsync();
        Assert.Equal(ComponentState.Disposed, component.LifecycleState);
    }

    static Exception[] Flatten(Exception error) => error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [error];
}
