using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class OwnerShutdownTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_reports_an_already_disposed_message_component_once(bool running)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        if (running) await fixture.Client.StartAsync(Token);
        await fixture.Client.MessageManager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Client.StopAsync(Token));
        Assert.Equal(ComponentState.Stopped, fixture.Client.LifecycleState);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Shutdown_drains_routes_from_later_child_work_and_retains_independent_errors(bool dispose, bool failWrite, bool failStop)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.StartAsync(Token);
        var manager = fixture.Client.MessageManager;
        await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).State == ResourceSyncState.CaughtUp);
        using var nextRelay = new OfflineRelay("next.test");
        fixture.Relay.LinkTo(nextRelay);
        nextRelay.Devices[fixture.Account.AccountId] = fixture.Client.DeviceManager.DeviceState!;
        nextRelay.Handler = (request, _) => request.Method == "account.route.publish"
            ? throw new HttpRequestException("publication offline") : Task.FromResult(nextRelay.Respond(request));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.AccountManager.PublishRouteAsync(nextRelay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token));
        var publishing = AsyncTest.Signal();
        var publish = AsyncTest.Signal();
        nextRelay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.publish")
            {
                publishing.TrySetResult();
                await publish.Task.WaitAsync(token);
            }
            return nextRelay.Respond(request);
        };
        await using var fault = failWrite ? await fixture.Database.CreateTriggerAsync("fail_timeline",
            "BEFORE INSERT ON AccountTimelines BEGIN SELECT RAISE(ABORT, 'timeline write failed'); END;") : null;
        var messageStopped = AsyncTest.Signal();
        var continueShutdown = AsyncTest.Signal();
        var accountStopped = AsyncTest.Signal();
        var observing = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var held = 0;
        var stopError = new IOException("message stop observer failed");
        var routeError = new IOException("route error observer failed");
        EventHandler<ComponentStateChangedEventArgs> onState = (_, change) =>
        {
            if (change.CurrentState != ComponentState.Stopped) return;
            messageStopped.TrySetResult();
            continueShutdown.Task.WaitAsync(Token).GetAwaiter().GetResult();
            if (failStop) throw stopError;
        };
        EventHandler<BackgroundErrorEventArgs> onError = (_, args) =>
        {
            if (args.Error is not DbUpdateException { InnerException.Message: var message }
                || !message.Contains("timeline write failed", StringComparison.Ordinal)) return;
            observing.TrySetResult();
            release.Task.WaitAsync(Token).GetAwaiter().GetResult();
            throw routeError;
        };
        manager.StateChanged += onState;
        manager.BackgroundError += onError;
        fixture.Client.AccountManager.StateChanged += (_, change) => { if (change.CurrentState == ComponentState.Stopped) accountStopped.TrySetResult(); };
        using var observer = new DatabaseObserver((name, data) =>
        {
            // Pause before acquiring the writer, leaving the account worker free to finish.
            if (failWrite || !name.EndsWith("ConnectionOpening", StringComparison.Ordinal)
                || data is not ConnectionEventData connection
                || connection.Connection.DataSource != fixture.Database.Options.Path
                || !Environment.StackTrace.Contains("PersistObservedRoutesAsync", StringComparison.Ordinal)
                || Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
            observing.TrySetResult();
            release.Task.WaitAsync(Token).GetAwaiter().GetResult();
        });
        Task? shutdown = null;
        var completed = false;
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await publishing.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            shutdown = dispose ? fixture.Client.DisposeAsync().AsTask() : fixture.Client.StopAsync(Token);
            await messageStopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            publish.TrySetResult();
            await observing.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            continueShutdown.TrySetResult();
            await accountStopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await Task.WhenAny(shutdown, Task.Delay(100, Token));
            Assert.False(shutdown.IsCompleted);
            if (fault is not null) await fault.DisposeAsync();
            release.TrySetResult();
            var failure = await Record.ExceptionAsync(() => shutdown.WaitAsync(TimeSpan.FromSeconds(10), Token));
            if (failWrite)
            {
                var failures = Assert.IsType<AggregateException>(failure).Flatten().InnerExceptions;
                Assert.Equal(failStop ? 3 : 2, failures.Count);
                Assert.Contains(routeError, failures);
                Assert.Contains(failures, error => error is DbUpdateException { InnerException.Message: var message } && message.Contains("timeline write failed", StringComparison.Ordinal));
                if (failStop) Assert.Contains(stopError, failures);
            }
            else Assert.Null(failure);
            manager.StateChanged -= onState;
            manager.BackgroundError -= onError;
            Assert.Equal(dispose ? ComponentState.Disposed : ComponentState.Stopped, fixture.Client.LifecycleState);
            if (!dispose)
            {
                await fixture.Client.StopAsync(Token);
                await fixture.Client.StartAsync(Token);
                await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(nextRelay.RelayId, Token)).State == ResourceSyncState.CaughtUp);
                await fixture.Client.StopAsync(Token);
            }
            await using var database = fixture.Database.Open();
            Assert.True(await database.AccountTimelines.AnyAsync(row => row.RelayId == nextRelay.RelayId, Token));
            completed = true;
        }
        finally
        {
            publish.TrySetResult();
            continueShutdown.TrySetResult();
            release.TrySetResult();
            manager.StateChanged -= onState;
            manager.BackgroundError -= onError;
            if (fault is not null) await fault.DisposeAsync();
            if (!completed)
            {
                if (shutdown is not null) _ = await Record.ExceptionAsync(() => shutdown.WaitAsync(TimeSpan.FromSeconds(10), Token));
                _ = await Record.ExceptionAsync(() => fixture.Client.DisposeAsync().AsTask());
            }
        }
    }
}
