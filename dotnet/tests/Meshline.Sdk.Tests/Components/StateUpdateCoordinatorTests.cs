using Meshline.Components;
using Meshline.Storage;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Components;

public sealed class StateUpdateCoordinatorTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resume_joins_active_intent_without_overwriting_or_canceling_its_owner()
    {
        using var time = Clock.Use(new ManualClock());
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var calls = 0;
        var coordinator = new TestStateUpdateCoordinator(database.Options, "group.update", async (attempt, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return attempt.Intent["name"]!.GetValue<string>();
        }, (_, error) => Assert.Fail(error.ToString()));
        var original = coordinator.SubmitAsync<string>("resource", new() { ["name"] = "user edit" }, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var joined = coordinator.ResumeAsync<string>("resource", new() { ["name"] = "default" }, cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);
            Assert.False(original.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("user edit", await original);
        await coordinator.DrainAsync();
    }

    [Fact]
    public async Task Superseded_initialization_cannot_replace_a_newer_in_memory_retry_after_storage_failure()
    {
        using var time = Clock.Use(new ManualClock());
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using (var write = database.Open())
            await write.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_schedule BEFORE UPDATE OF NextAttemptAt ON GroupOperations WHEN NEW.Generation = 2 AND OLD.Generation = 2 BEGIN SELECT RAISE(ABORT, 'schedule write failed'); END", Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var coordinator = new TestStateUpdateCoordinator(database.Options, "group.update", async (attempt, _) =>
        {
            if (attempt.Intent["name"]!.GetValue<string>() == "initial")
            {
                entered.TrySetResult(); await release.Task;
                throw new InvalidOperationException("late rejection");
            }
            throw new HttpRequestException("latest network failure");
        }, (_, error) => Assert.Fail(error.ToString()));
        var initial = coordinator.ResumeAsync<string>("resource", new() { ["name"] = "initial" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<AggregateException>(() => coordinator.SubmitAsync<string>("resource", new() { ["name"] = "new" }, Token));
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => initial);
            Assert.Equal(Clock.UtcNow.AddSeconds(1), await coordinator.ReadNextAttemptAsync("resource", Token));
            release.TrySetResult(); await coordinator.DrainAsync();
            Assert.Equal(Clock.UtcNow.AddSeconds(1), await coordinator.ReadNextAttemptAsync("resource", Token));
            await using var read = database.Open();
            var row = await read.GroupOperations.SingleAsync(Token);
            Assert.Equal(2, row.Generation);
            Assert.True(row.Pending);
        }
        finally { release.TrySetResult(); await coordinator.DrainAsync(); }
    }

    [Fact]
    public async Task Failed_pause_write_keeps_the_live_generation_paused_until_a_new_call()
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using (var write = database.Open())
            await write.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_pause BEFORE UPDATE OF NextAttemptAt ON GroupOperations WHEN NEW.GroupId = 'resource' AND NEW.Generation = 1 BEGIN SELECT RAISE(ABORT, 'injected pause failure'); END", Token);
        var rejected = 0;
        var probes = 0;
        var coordinator = new TestStateUpdateCoordinator(database.Options, "group.update", (attempt, _) =>
        {
            var name = attempt.Intent["name"]!.GetValue<string>();
            if (name == "old") { Interlocked.Increment(ref rejected); throw new InvalidOperationException("permission denied"); }
            if (name == "probe" && Interlocked.Increment(ref probes) == 1) throw new HttpRequestException("offline");
            return Task.FromResult<object?>(attempt.Intent.ToJsonString());
        }, (_, error) => Assert.Fail(error.ToString()));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => coordinator.SubmitAsync<string>("resource", new() { ["name"] = "old", ["bio"] = "retained" }, Token));
        Assert.IsType<InvalidOperationException>(failure.InnerExceptions[0]);
        Assert.IsType<DbUpdateException>(failure.InnerExceptions[1]);
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.SubmitAsync<string>("probe", new() { ["name"] = "probe" }, Token));
        await coordinator.DrainAsync();
        using var runtime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        coordinator.Start(runtime.Token);
        try
        {
            await AsyncTest.UntilAsync(() => clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)) || Volatile.Read(ref rejected) > 1);
            Assert.Equal(1, Volatile.Read(ref rejected));
            clock.Advance(TimeSpan.FromSeconds(1));
            await AsyncTest.UntilAsync(async () =>
            {
                await using var read = database.Open();
                return !(await read.GroupOperations.SingleAsync(value => value.GroupId == "probe", Token)).Pending;
            });
            Assert.Equal(1, Volatile.Read(ref rejected));
            var latest = await coordinator.SubmitAsync<string>("resource", new() { ["name"] = "latest" }, Token);
            Assert.Equal("retained", JsonNode.Parse(latest)!["bio"]!.GetValue<string>());
        }
        finally { await runtime.CancelAsync(); await coordinator.StopAsync(); await coordinator.DrainAsync(); }
        await using var verify = database.Open();
        var row = await verify.GroupOperations.SingleAsync(value => value.GroupId == "resource", Token);
        Assert.Equal(2, row.Generation);
        Assert.False(row.Pending);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("restart")]
    [InlineData("repair")]
    public async Task Failed_retry_schedule_write_does_not_spin_and_preserves_latest_recovery(string recovery)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var calls = 0;
        var available = false;
        var restart = recovery == "restart";
        var failed = AsyncTest.Signal();
        var coordinator = new TestStateUpdateCoordinator(database.Options, "group.update", (attempt, _) =>
        {
            Interlocked.Increment(ref calls);
            if (!Volatile.Read(ref available)) throw new HttpRequestException("offline");
            return Task.FromResult<object?>(attempt.Intent.ToJsonString());
        }, (_, _) => failed.TrySetResult());
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.SubmitAsync<string>("resource", new() { ["name"] = "old", ["bio"] = "retained" }, Token));
        await coordinator.DrainAsync();
        await using (var write = database.Open())
            await write.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_schedule BEFORE UPDATE OF NextAttemptAt ON GroupOperations WHEN NEW.Generation = 1 BEGIN SELECT RAISE(ABORT, 'injected schedule failure'); END", Token);
        using var runtime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        coordinator.Start(runtime.Token);
        try
        {
            await AsyncTest.UntilAsync(() => clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
            clock.Advance(TimeSpan.FromSeconds(1));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await AsyncTest.UntilAsync(() => clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)) || Volatile.Read(ref calls) > 2);
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.False(clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
            if (!restart) Volatile.Write(ref available, true);
            if (recovery == "repair")
            {
                await using (var write = database.Open()) await write.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_schedule", Token);
                clock.Advance(TimeSpan.FromSeconds(2));
                await AsyncTest.UntilAsync(async () =>
                {
                    await using var verify = database.Open();
                    return !(await verify.GroupOperations.SingleAsync(Token)).Pending;
                });
            }
            else
            {
                var latest = coordinator.SubmitAsync<string>("resource", new() { ["name"] = "latest" }, Token);
                if (restart) await Assert.ThrowsAsync<HttpRequestException>(() => latest.WaitAsync(TimeSpan.FromSeconds(5), Token));
                else Assert.Equal("retained", JsonNode.Parse(await latest.WaitAsync(TimeSpan.FromSeconds(5), Token))!["bio"]!.GetValue<string>());
            }
            Assert.Equal(3, Volatile.Read(ref calls));
        }
        finally
        {
            await runtime.CancelAsync();
            await coordinator.StopAsync();
            await coordinator.DrainAsync();
        }
        if (restart)
        {
            var recovered = new ConcurrentQueue<string>();
            var reopened = new TestStateUpdateCoordinator(database.Options, "group.update", (attempt, _) =>
            {
                recovered.Enqueue(attempt.Intent.ToJsonString());
                return Task.FromResult<object?>(null);
            }, (_, error) => Assert.Fail(error.ToString()));
            using var resumed = CancellationTokenSource.CreateLinkedTokenSource(Token);
            reopened.Start(resumed.Token);
            try
            {
                await AsyncTest.UntilAsync(() => clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
                clock.Advance(TimeSpan.FromSeconds(1));
                await AsyncTest.UntilAsync(async () =>
                {
                    await using var verify = database.Open();
                    return !(await verify.GroupOperations.SingleAsync(Token)).Pending;
                });
            }
            finally { await resumed.CancelAsync(); await reopened.StopAsync(); await reopened.DrainAsync(); }
            var intent = JsonNode.Parse(Assert.Single(recovered))!;
            Assert.Equal("latest", intent["name"]!.GetValue<string>());
            Assert.Equal("retained", intent["bio"]!.GetValue<string>());
        }
        await using var read = database.Open();
        var row = await read.GroupOperations.SingleAsync(value => value.GroupId == "resource", Token);
        Assert.Equal(recovery == "repair" ? 1 : 2, row.Generation);
        Assert.False(row.Pending);
        Assert.Null(row.IntentJson);
    }

    [Fact]
    public async Task Supersession_does_not_wait_for_cancellation_callbacks_but_draining_does()
    {
        using var time = Clock.Use(new ManualClock());
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var finish = AsyncTest.Signal();
        using var release = new ManualResetEventSlim();
        var coordinator = new TestStateUpdateCoordinator(database.Options, "group.update", async (attempt, token) =>
        {
            if (attempt.Intent["name"]!.GetValue<string>() == "old")
            {
                token.Register(() => { canceled.TrySetResult(); release.Wait(); });
                entered.TrySetResult();
                await finish.Task;
            }
            return attempt.Intent["name"]!.GetValue<string>();
        }, (_, error) => Assert.Fail(error.ToString()));
        var first = coordinator.SubmitAsync<string>("resource", new() { ["name"] = "old" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal("latest", await coordinator.SubmitAsync<string>("resource", new() { ["name"] = "latest" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => first);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            finish.TrySetResult();
            var drain = coordinator.DrainAsync();
            // Drain must still be waiting once publication work has had a chance to finish.
            await Task.WhenAny(drain, Task.Delay(TimeSpan.FromMilliseconds(100), Token));
            Assert.False(drain.IsCompleted);
            release.Set();
            await drain.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        finally { release.Set(); finish.TrySetResult(); await coordinator.DrainAsync(); }
    }

    [Theory]
    [InlineData(5_184_000)]
    [InlineData(9_007_199_254_740_991)]
    public async Task Long_retry_hints_survive_restart_without_overflow_or_repeated_scheduler_errors(long retryAfter)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var first = new TestStateUpdateCoordinator(database.Options, "group.update", (_, _) => throw new RelayException(new()
        {
            Code = "rate_limited",
            Message = "wait",
            Data = ImmutableDictionary<string, JsonElement>.Empty.Add("retry_after", JsonSerializer.SerializeToElement(retryAfter))
        }), (_, error) => Assert.Fail(error.ToString()));
        await Assert.ThrowsAsync<RelayException>(() => first.SubmitAsync<string>("resource", new() { ["name"] = "latest" }, Token));
        await first.DrainAsync();
        await using (var read = database.Open())
            Assert.True((await read.GroupOperations.SingleAsync(Token)).NextAttemptAt > clock.GetUtcNow().AddDays(59));

        var errors = new ConcurrentQueue<Exception>();
        var publications = 0;
        var restarted = new TestStateUpdateCoordinator(database.Options, "group.update", (_, _) =>
        {
            Interlocked.Increment(ref publications);
            return Task.FromResult<object?>("latest");
        }, (_, error) => errors.Enqueue(error));
        using var runtime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        restarted.Start(runtime.Token);
        try
        {
            await AsyncTest.UntilAsync(() => clock.ActiveTimers != 0);
            Assert.Empty(errors);
            Assert.False(clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Equal("latest", await restarted.SubmitAsync<string>("resource", new() { ["name"] = "latest" }, Token));
            Assert.Equal(1, Volatile.Read(ref publications));
        }
        finally { await runtime.CancelAsync(); await restarted.StopAsync(); await restarted.DrainAsync(); }
    }

    [Theory]
    [InlineData("profile.publish")]
    [InlineData("group.update")]
    [InlineData("channel.update")]
    public async Task First_admission_without_a_signature_persists_an_unprepared_request(string method)
    {
        using var time = Clock.Use(new ManualClock());
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var entered = AsyncTest.Signal();
        var coordinator = new TestStateUpdateCoordinator(database.Options, method, async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }, (_, _) => { }, accountId: "resource");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var update = coordinator.SubmitAsync<object>("resource", new() { ["relay"] = "relay", ["name"] = "latest" }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using var read = database.Open();
            IStatePublication row = method switch
            {
                "group.update" => await read.GroupOperations.SingleAsync(Token),
                "channel.update" => await read.ChannelOperations.SingleAsync(Token),
                _ => await read.SignedRequests.SingleAsync(Token)
            };
            Assert.True(row.Pending);
            Assert.NotNull(row.IntentJson);
            Assert.Null(row.RequestJson);
            Assert.Equal(PublicationRequestState.None, row.RequestState);
        }
        finally { await cancellation.CancelAsync(); await coordinator.DrainAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
    }

    [Fact]
    public async Task Supersession_during_uncancelable_preparation_preserves_fields_and_fences_the_old_write()
    {
        using var time = Clock.Use(new ManualClock());
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var writes = new List<string>();
        var coordinator = new TestStateUpdateCoordinator(database.Options, "group.update", async (attempt, _) =>
        {
            if (attempt.Intent["name"]!.GetValue<string>() == "old")
            {
                entered.TrySetResult();
                await release.Task; // A wallet may ignore cancellation while waiting for approval.
            }
            await attempt.CurrentAsync(() =>
            {
                writes.Add(attempt.Intent["name"]!.GetValue<string>());
                return Task.CompletedTask;
            }, CancellationToken.None);
            return attempt.Intent.ToJsonString();
        }, (_, _) => { });
        var first = coordinator.SubmitAsync<string>("resource", new() { ["name"] = "old", ["bio"] = "retained" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await coordinator.SubmitAsync<string>("resource", new() { ["name"] = "latest" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => first);
            Assert.Equal("retained", JsonNode.Parse(latest)!["bio"]!.GetValue<string>());
        }
        finally { release.TrySetResult(); }
        await coordinator.DrainAsync();
        Assert.Equal(["latest"], writes);
    }

    [Fact]
    public async Task Failed_admission_preserves_the_previous_waiter_and_durable_intent()
    {
        using var time = Clock.Use(new ManualClock());
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var coordinator = new TestStateUpdateCoordinator(database.Options, "group.update", async (attempt, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            await attempt.CheckAsync(Token);
            return attempt.Intent["name"]!.GetValue<string>();
        }, (_, _) => { });
        var first = coordinator.SubmitAsync<string>("resource", new() { ["name"] = "original" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using var write = database.Open();
            await write.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_update BEFORE UPDATE ON GroupOperations BEGIN SELECT RAISE(ABORT, 'injected admission failure'); END", Token);
            await Assert.ThrowsAsync<DbUpdateException>(() => coordinator.SubmitAsync<string>("resource", new() { ["name"] = "lost" }, Token));
            Assert.False(first.IsCompleted);
            var record = await write.GroupOperations.AsNoTracking().SingleAsync(Token);
            Assert.Equal(1, record.Generation);
            Assert.Equal("original", JsonNode.Parse(record.IntentJson!)!["name"]!.GetValue<string>());
            await write.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_update", Token);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("original", await first);
        await coordinator.DrainAsync();
    }

    [Fact]
    public async Task Restart_keeps_permanent_failures_paused_until_a_new_call_merges_and_resumes_them()
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var first = new TestStateUpdateCoordinator(database.Options, "group.update", (attempt, _) =>
            throw (attempt.Intent["name"]!.GetValue<string>() == "paused"
                ? new InvalidOperationException("permission denied") : new HttpRequestException("offline")), (_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.SubmitAsync<string>("paused", new() { ["name"] = "paused", ["bio"] = "retained" }, Token));
        await Assert.ThrowsAsync<HttpRequestException>(() => first.SubmitAsync<string>("probe", new() { ["name"] = "probe" }, Token));
        await first.DrainAsync();
        var publications = new ConcurrentQueue<string>();
        var restarted = new TestStateUpdateCoordinator(database.Options, "group.update", (attempt, _) =>
        {
            publications.Enqueue(attempt.Intent["name"]!.GetValue<string>());
            return Task.FromResult<object?>(attempt.Intent.ToJsonString());
        }, (_, error) => Assert.Fail(error.ToString()));
        using var runtime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        restarted.Start(runtime.Token);
        try
        {
            await AsyncTest.UntilAsync(() => clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
            clock.Advance(TimeSpan.FromSeconds(1));
            // Completion of another resource proves the restart scan has run.
            await AsyncTest.UntilAsync(async () =>
            {
                await using var read = database.Open();
                return !(await read.GroupOperations.SingleAsync(value => value.GroupId == "probe", Token)).Pending;
            });
            Assert.Equal(["probe"], publications.ToArray());
            await using (var read = database.Open())
            {
                var paused = await read.GroupOperations.SingleAsync(value => value.GroupId == "paused", Token);
                Assert.True(paused.Pending);
                Assert.Null(paused.NextAttemptAt);
                Assert.NotNull(paused.IntentJson);
            }
            var result = await restarted.SubmitAsync<string>("paused", new() { ["name"] = "resumed" }, Token);
            Assert.Equal("retained", JsonNode.Parse(result)!["bio"]!.GetValue<string>());
            Assert.Equal(["probe", "resumed"], publications.ToArray());
        }
        finally
        {
            await runtime.CancelAsync();
            await restarted.StopAsync();
            await restarted.DrainAsync();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task Restart_honors_the_saved_deadline_and_resets_only_the_backoff_counter(int retryAfter)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var secondFailure = AsyncTest.Signal();
        var first = new TestStateUpdateCoordinator(database.Options, "group.update", (_, _) => throw new RelayException(new()
        {
            Code = "rate_limited",
            Message = "wait",
            Data = ImmutableDictionary<string, JsonElement>.Empty.Add("retry_after", JsonSerializer.SerializeToElement(retryAfter))
        }), (_, _) => secondFailure.TrySetResult());
        await Assert.ThrowsAsync<RelayException>(() => first.SubmitAsync<string>("resource", new() { ["name"] = "latest" }, Token));
        using (var runtime = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            first.Start(runtime.Token);
            try
            {
                var initialDelay = TimeSpan.FromSeconds(Math.Max(1, retryAfter));
                await AsyncTest.UntilAsync(() => clock.HasTimerDueWithin(initialDelay));
                clock.Advance(initialDelay);
                await secondFailure.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
                await using var read = database.Open();
                Assert.Equal(clock.GetUtcNow().AddSeconds(Math.Max(2, retryAfter)), (await read.GroupOperations.SingleAsync(Token)).NextAttemptAt);
            }
            finally { await runtime.CancelAsync(); await first.StopAsync(); await first.DrainAsync(); }
        }
        // Reopen one second before the persisted deadline. A restart must not send early.
        clock.Advance(TimeSpan.FromSeconds(Math.Max(2, retryAfter) - 1));
        var publications = 0;
        var failed = AsyncTest.Signal();
        var restarted = new TestStateUpdateCoordinator(database.Options, "group.update", (_, _) =>
        {
            Interlocked.Increment(ref publications);
            throw new HttpRequestException("still offline");
        }, (_, _) => failed.TrySetResult());
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(Token);
        restarted.Start(recovery.Token);
        try
        {
            await AsyncTest.UntilAsync(() => clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
            Assert.False(clock.HasTimerDueWithin(TimeSpan.FromMilliseconds(500)));
            Assert.Equal(0, Volatile.Read(ref publications));
            clock.Advance(TimeSpan.FromSeconds(1));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(1, Volatile.Read(ref publications));
            await using var read = database.Open();
            Assert.Equal(clock.GetUtcNow().AddSeconds(1), (await read.GroupOperations.SingleAsync(Token)).NextAttemptAt);
        }
        finally { await recovery.CancelAsync(); await restarted.StopAsync(); await restarted.DrainAsync(); }
    }

    [Fact]
    public async Task Cancellation_after_admission_keeps_the_intent_for_restart_and_other_resources_progress()
    {
        using var time = Clock.Use(new ManualClock());
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var entered = AsyncTest.Signal();
        var coordinator = new TestStateUpdateCoordinator(database.Options, "group.update", async (attempt, token) =>
        {
            if (attempt.Intent["name"]!.GetValue<string>() == "retained")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return "done";
        }, (_, _) => { });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = coordinator.SubmitAsync<string>("one", new() { ["name"] = "retained" }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("done", await coordinator.SubmitAsync<string>("two", new() { ["name"] = "independent" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await coordinator.DrainAsync();
        await AsyncTest.UntilAsync(async () =>
        {
            await using var read = database.Open();
            return (await read.GroupOperations.SingleAsync(value => value.GroupId == "one", Token)).NextAttemptAt > Clock.UtcNow;
        });
        var recovered = AsyncTest.Signal();
        var restarted = new TestStateUpdateCoordinator(database.Options, "group.update", (attempt, _) =>
        {
            Assert.Equal("retained", attempt.Intent["name"]!.GetValue<string>());
            recovered.TrySetResult();
            return Task.FromResult<object?>("done");
        }, (_, error) => Assert.Fail(error.ToString()));
        using var runtime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        restarted.Start(runtime.Token);
        await AsyncTest.UntilAsync(() => ((ManualClock)Clock.Provider).HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        ((ManualClock)Clock.Provider).Advance(TimeSpan.FromSeconds(1));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await runtime.CancelAsync();
        await restarted.StopAsync();
        await restarted.DrainAsync();
    }
}
