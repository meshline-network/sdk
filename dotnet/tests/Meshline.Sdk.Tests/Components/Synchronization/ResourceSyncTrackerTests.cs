using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Meshline.Tests.Components.Synchronization;

public sealed class ResourceSyncTrackerTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ResourceSyncState.CaughtUp)]
    [InlineData(ResourceSyncState.Blocked)]
    [InlineData(ResourceSyncState.Idle)]
    public async Task Manual_completion_remains_observable_after_stop_but_disposal_invalidates_it(ResourceSyncState expected)
    {
        using var time = Clock.Use(new ManualClock());
        var tracker = new ResourceSyncTracker();
        var changes = new List<ResourceSyncStatus>();
        var release = AsyncTest.Signal();
        using var cancellation = new CancellationTokenSource();
        var pass = tracker.RunAsync("manual", async () =>
        {
            await release.Task;
            if (expected == ResourceSyncState.Blocked) throw new SqliteException("disk failure", 10);
            return null;
        }, changes.Add, cancellation.Token, preserveOnStop: true);
        tracker.ObserveGap("manual");
        tracker.Stop(changes.Add);
        Assert.Equal(ResourceSyncState.Synchronizing, tracker.Get("manual").State);
        if (expected == ResourceSyncState.Idle) cancellation.Cancel();
        release.TrySetResult();
        if (expected == ResourceSyncState.CaughtUp) await pass;
        else if (expected == ResourceSyncState.Blocked) await Assert.ThrowsAsync<SqliteException>(() => pass);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        var completed = tracker.Get("manual");
        Assert.Equal(expected, completed.State);
        Assert.True(completed.HasRetentionGap);
        Assert.Same(completed, changes[^1]);
        tracker.Stop(changes.Add);
        Assert.Same(completed, tracker.Get("manual"));
        await tracker.RunAsync("manual", () => { tracker.Stop(changes.Add, resetAll: true); return Task.FromResult<SyncBlock?>(null); }, changes.Add, Token, preserveOnStop: true);
        Assert.Equal(ResourceSyncState.Idle, tracker.Get("manual").State);
        Assert.Equal(completed.LastSynchronizedAt, tracker.Get("manual").LastSynchronizedAt);
    }

    [Fact]
    public async Task Incomplete_or_superseded_passes_never_update_the_completion_time()
    {
        using var time = Clock.Use(new ManualClock());
        var tracker = new ResourceSyncTracker();
        var pending = new TaskCompletionSource<SyncBlock?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPass = tracker.RunAsync("group", () => pending.Task, _ => { }, Token);
        Assert.Null(tracker.Get("group").LastSynchronizedAt);
        var failure = new SqliteException("disk failure", 10);
        await Assert.ThrowsAsync<SqliteException>(() => tracker.RunAsync("group", () => Task.FromException<SyncBlock?>(failure), _ => { }, Token));
        Assert.Same(failure, tracker.Get("group").Error);
        Assert.Equal(ResourceSyncBlockReason.Storage, tracker.Get("group").BlockReason);
        pending.SetResult(null);
        await oldPass;
        Assert.Equal(ResourceSyncState.Blocked, tracker.Get("group").State);
        Assert.Null(tracker.Get("group").LastSynchronizedAt);
        using var cancellation = new CancellationTokenSource();
        var canceled = tracker.RunAsync("other", async () => { await Task.Delay(Timeout.Infinite, cancellation.Token); return null; }, _ => { }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(ResourceSyncState.Idle, tracker.Get("other").State);
        Assert.Null(tracker.Get("other").Error);
    }
}
