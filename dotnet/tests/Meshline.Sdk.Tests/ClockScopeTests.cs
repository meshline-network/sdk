using Meshline.Tests.Support;

namespace Meshline.Tests;

public sealed class ClockScopeTests
{
    [Fact]
    public void Default_and_nested_scopes_restore_previous_provider()
    {
        Assert.Same(TimeProvider.System, Clock.Provider);

        var first = new ManualClock();
        var second = new ManualClock();
        second.Advance(TimeSpan.FromDays(1));
        using (Clock.Use(first))
        {
            Assert.Same(first, Clock.Provider);

            using (Clock.Use(second))
                Assert.Equal(second.GetUtcNow(), Clock.UtcNow);

            Assert.Same(first, Clock.Provider);
        }

        Assert.Same(TimeProvider.System, Clock.Provider);
    }

    [Fact]
    public async Task Parallel_async_flows_are_isolated_and_background_tasks_inherit_the_scope()
    {
        var arrived = 0;
        var ready = AsyncTest.Signal();
        var tasks = Enumerable.Range(1, 8).Select(index => Task.Run(async () =>
        {
            var clock = new ManualClock();
            clock.Advance(TimeSpan.FromDays(index));
            using var scope = Clock.Use(clock);
            if (Interlocked.Increment(ref arrived) == 8)
                ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(false);

            Assert.Same(clock, Clock.Provider);
            Assert.Equal(clock.GetUtcNow(), await Task.Run(() => Clock.UtcNow, TestContext.Current.CancellationToken));
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        Assert.Same(TimeProvider.System, Clock.Provider);
    }

    [Fact]
    public async Task Virtual_delay_uses_the_scope_clock_without_affecting_other_clocks()
    {
        var clock = new ManualClock();
        using var scope = Clock.Use(clock);
        var delay = Task.Delay(TimeSpan.FromMinutes(5), Clock.Provider, TestContext.Current.CancellationToken);
        new ManualClock().Advance(TimeSpan.FromHours(1));

        Assert.False(delay.IsCompleted);

        clock.Advance(TimeSpan.FromMinutes(5));
        await delay.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
