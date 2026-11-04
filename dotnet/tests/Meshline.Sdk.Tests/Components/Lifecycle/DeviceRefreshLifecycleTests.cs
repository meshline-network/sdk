using Meshline.Components;
using Meshline.Tests.Support;
using System.Runtime.ExceptionServices;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class DeviceRefreshLifecycleTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_route_is_reported_while_running_and_observer_failures_remain_visible_at_shutdown(bool failObserver)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(2) }, Token);
        await fixture.Client.StartAsync(Token);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerError = new InvalidOperationException("background observer failed");
        fixture.Client.DeviceManager.BackgroundError += (_, args) =>
        {
            reported.TrySetResult(args.Error);
            if (failObserver) throw observerError;
        };

        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(await fixture.Client.AccountManager.RefreshRouteAsync(cancellationToken: Token));
        var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("account route could not be found", error.Message, StringComparison.Ordinal);

        if (failObserver)
            Assert.Same(observerError, await Record.ExceptionAsync(() => fixture.Client.StopAsync(Token)));
        else
            await fixture.Client.StopAsync(Token);
        Assert.Equal(ComponentState.Stopped, fixture.Client.LifecycleState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_drains_a_missing_route_failure_that_races_runtime_cancellation(bool dispose)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(2) }, Token);
        await fixture.Client.StartAsync(Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var queryEntered = AsyncTest.Signal();
        var queryCanceled = AsyncTest.Signal();
        var paused = 0;
        var released = false;
        Task? shutdown = null;

        // Query cancellation begins in the stop hook, after runtime cancellation.
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                queryEntered.TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, queryCanceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        var query = fixture.Client.DeviceManager.RefreshDeviceStateAsync(cancellationToken: Token);
        await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Pause the real domain failure before exception filters run. The scoped clock
        // isolates this process-wide diagnostic hook from concurrently running tests.
        void PauseFailure(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (!ReferenceEquals(Clock.Provider, clock)
                || args.Exception is not InvalidOperationException { Message: "The account route could not be found. Establish or recover the account explicitly." }
                || Interlocked.Exchange(ref paused, 1) != 0)
                return;
            entered.TrySetResult();
            released = release.Task.Wait(TimeSpan.FromSeconds(10));
        }

        AppDomain.CurrentDomain.FirstChanceException += PauseFailure;
        try
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.Null(await fixture.Client.AccountManager.RefreshRouteAsync(cancellationToken: Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

            shutdown = dispose ? fixture.Client.DisposeAsync().AsTask() : fixture.Client.StopAsync(Token);
            await queryCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            AppDomain.CurrentDomain.FirstChanceException -= PauseFailure;
            if (shutdown is not null)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(10), Token));
                await shutdown.WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
        }

        Assert.True(released);
        Assert.Equal(dispose ? ComponentState.Disposed : ComponentState.Stopped, fixture.Client.LifecycleState);
        if (!dispose)
        {
            fixture.Relay.Handler = null;
            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
            await fixture.Client.StartAsync(Token);
            await fixture.Client.StopAsync(Token);
        }
    }
}
