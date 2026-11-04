using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class RouteRecorderLifecycleTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Restart_retries_route_records_after_storage_and_observer_failures(bool throwingObserver, bool initiallyRunning)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var nextRelay = new OfflineRelay("recovered.test");
        fixture.Relay.LinkTo(nextRelay);
        nextRelay.Devices[fixture.Account.AccountId] = fixture.Client.DeviceManager.DeviceState!;
        var manager = fixture.Client.MessageManager;
        await manager.StopAsync(Token);
        if (initiallyRunning)
        {
            await manager.StartAsync(Token);
            await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).State == ResourceSyncState.CaughtUp);
        }
        await using var fault = await FailWritesAsync(fixture);
        var observerError = new IOException("route recorder observer failed");
        var reported = AsyncTest.Signal();
        EventHandler<BackgroundErrorEventArgs> handler = (_, _) =>
        {
            reported.TrySetResult();
            if (throwingObserver) throw observerError;
        };
        manager.BackgroundError += handler;
        var completed = false;
        try
        {
            await fixture.Client.AccountManager.PublishRouteAsync(nextRelay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            manager.BackgroundError -= handler;
            var failure = await Record.ExceptionAsync(() => manager.StopAsync(Token));
            if (throwingObserver) AssertFailure(failure, observerError);
            else Assert.Null(failure);
            await manager.StopAsync(Token);
            await fault.DisposeAsync();

            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            manager.BackgroundError += (_, args) => errors.Enqueue(args.Error);
            await manager.StartAsync(Token);
            await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(nextRelay.RelayId, Token)).State == ResourceSyncState.CaughtUp);
            await manager.StopAsync(Token);
            await using (var database = fixture.Database.Open())
                Assert.True(await database.AccountTimelines.AnyAsync(row => row.RelayId == nextRelay.RelayId, Token));
            Assert.Empty(errors);
            await manager.DisposeAsync();
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
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Queued_routes_recover_without_losing_the_previous_failure(bool dispose, bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var firstRelay = new OfflineRelay("first.test");
        using var secondRelay = new OfflineRelay("second.test");
        fixture.Relay.LinkTo(firstRelay);
        fixture.Relay.LinkTo(secondRelay);
        var manager = fixture.Client.MessageManager;
        await manager.StopAsync(Token);
        await using var fault = await FailWritesAsync(fixture);
        Exception observerError = cancellationError ? new OperationCanceledException("observer canceled") : new IOException("observer failed");
        var reported = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        EventHandler<BackgroundErrorEventArgs> handler = (_, _) =>
        {
            reported.TrySetResult();
            release.Task.WaitAsync(Token).GetAwaiter().GetResult();
            throw observerError;
        };
        manager.BackgroundError += handler;
        var completed = false;
        try
        {
            try
            {
                await fixture.Client.AccountManager.PublishRouteAsync(firstRelay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
                await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                manager.BackgroundError -= handler;
                await fault.DisposeAsync();
                // Queue another observation before the previous callback has returned.
                await fixture.Client.AccountManager.PublishRouteAsync(secondRelay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
            }
            finally { release.TrySetResult(); }
            await AsyncTest.UntilAsync(async () =>
            {
                await using var database = fixture.Database.Open();
                return await database.AccountTimelines.CountAsync(row => row.RelayId == firstRelay.RelayId || row.RelayId == secondRelay.RelayId, Token) == 2;
            });
            var failure = await Record.ExceptionAsync(() => dispose ? manager.DisposeAsync().AsTask() : manager.StopAsync(Token));
            AssertFailure(failure, observerError);
            if (!dispose) await manager.StopAsync(Token);
            await manager.DisposeAsync();
            completed = true;
        }
        finally
        {
            release.TrySetResult();
            manager.BackgroundError -= handler;
            await fault.DisposeAsync();
            if (!completed) _ = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task Stop_preserves_failures_from_route_work_admitted_after_its_snapshot()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var firstRelay = new OfflineRelay("first.test");
        using var secondRelay = new OfflineRelay("second.test");
        fixture.Relay.LinkTo(firstRelay);
        fixture.Relay.LinkTo(secondRelay);
        var manager = fixture.Client.MessageManager;
        await manager.StopAsync(Token);
        await using var fault = await FailWritesAsync(fixture);
        var firstError = new IOException("first observer failed");
        var secondError = new IOException("second observer failed");
        var firstReported = AsyncTest.Signal();
        var secondReported = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var calls = 0;
        EventHandler<BackgroundErrorEventArgs> handler = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstReported.TrySetResult();
                release.Task.WaitAsync(Token).GetAwaiter().GetResult();
                throw firstError;
            }
            secondReported.TrySetResult();
            throw secondError;
        };
        manager.BackgroundError += handler;
        var completed = false;
        try
        {
            Task stopping;
            try
            {
                await fixture.Client.AccountManager.PublishRouteAsync(firstRelay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
                await firstReported.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                stopping = manager.StopAsync(Token);
                Assert.False(stopping.IsCompleted);
                await fixture.Client.AccountManager.PublishRouteAsync(secondRelay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
            }
            finally { release.TrySetResult(); }
            var firstFailure = await Record.ExceptionAsync(() => stopping);
            AssertFailure(firstFailure, firstError);
            Assert.DoesNotContain(secondError, ((AggregateException)firstFailure!).Flatten().InnerExceptions);
            await secondReported.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var secondFailure = await Record.ExceptionAsync(() => manager.StopAsync(Token));
            AssertFailure(secondFailure, secondError);
            Assert.DoesNotContain(firstError, ((AggregateException)secondFailure!).Flatten().InnerExceptions);
            manager.BackgroundError -= handler;
            await manager.StopAsync(Token);
            await fault.DisposeAsync();
            await manager.DisposeAsync();
            await using var database = fixture.Database.Open();
            Assert.Equal(2, await database.AccountTimelines.CountAsync(row => row.RelayId == firstRelay.RelayId || row.RelayId == secondRelay.RelayId, Token));
            completed = true;
        }
        finally
        {
            release.TrySetResult();
            manager.BackgroundError -= handler;
            await fault.DisposeAsync();
            if (!completed) _ = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task Initialization_can_retry_after_a_route_recorder_failure()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.MessageManager.DisposeAsync();
        await using (var database = fixture.Database.Open())
            await database.AccountTimelines.ExecuteDeleteAsync(Token);
        await using var manager = new MessageManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Client.AccountManager, fixture.Client.DeviceManager, fixture.Protector);
        var observerError = new IOException("initial route observer failed");
        EventHandler<BackgroundErrorEventArgs> handler = (_, _) => throw observerError;
        manager.BackgroundError += handler;
        await using var fault = await FailWritesAsync(fixture);
        var completed = false;
        try
        {
            AssertFailure(await Record.ExceptionAsync(() => manager.InitializeAsync(Token)), observerError);
            Assert.Equal(ComponentState.Uninitialized, manager.LifecycleState);
            manager.BackgroundError -= handler;
            await fault.DisposeAsync();
            await manager.InitializeAsync(Token);
            Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
            await using (var database = fixture.Database.Open())
                Assert.True(await database.AccountTimelines.AnyAsync(row => row.RelayId == fixture.Relay.RelayId, Token));
            AssertFailure(await Record.ExceptionAsync(() => manager.StopAsync(Token)), observerError);
            await manager.StopAsync(Token);
            await manager.DisposeAsync();
            completed = true;
        }
        finally
        {
            manager.BackgroundError -= handler;
            await fault.DisposeAsync();
            if (!completed) _ = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask());
        }
    }

    static Task<IAsyncDisposable> FailWritesAsync(TestClient fixture) => fixture.Database.CreateTriggerAsync("fail_timeline",
        "BEFORE INSERT ON AccountTimelines BEGIN SELECT RAISE(ABORT, 'timeline write failed'); END;");

    static void AssertFailure(Exception? failure, Exception observerError)
    {
        Assert.NotNull(failure);
        var errors = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [failure];
        Assert.Single(errors, error => ReferenceEquals(error, observerError));
        Assert.Contains(errors, error => error is DbUpdateException { InnerException.Message: var message } && message.Contains("timeline write failed", StringComparison.Ordinal));
    }
}
