using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class OutcomeFailureTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Profile_observer_failure_after_caller_cancellation_is_drained_without_rolling_back_confirmed_profile()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var entered = AsyncTest.Signal();
        var sending = AsyncTest.Signal();
        var respond = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish")
            {
                sending.TrySetResult();
                await respond.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        using var release = new ManualResetEventSlim();
        var observerError = new IOException("profile observer failed");
        manager.ProfileChanged += (_, _) =>
        {
            entered.TrySetResult();
            release.Wait(Token);
            throw observerError;
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = manager.UpdateProfileAsync(new() { Nickname = "confirmed" }, caller.Token);
        try
        {
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            respond.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.Equal("confirmed", manager.Profile!.Nickname);
            release.Set();
            Assert.Same(observerError, await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token)));
        }
        finally { respond.TrySetResult(); release.Set(); }
        await fixture.ReopenAsync();
        Assert.Equal("confirmed", fixture.Client.Profile!.Nickname);
        Assert.Null((await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).PendingUpdate);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rejected_operations_retain_the_rejection_when_local_cleanup_fails(bool channel, bool failCleanup)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        Func<Task> close;
        if (channel)
        {
            var host = new ChannelRelay(fixture);
            host.Install();
            var created = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
            close = () => fixture.Client.ChannelManager.CloseChannelAsync(created.Ref, Token);
            fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "channel.close" ? OfflineRelay.Error("forbidden") : host.Respond(request));
        }
        else
        {
            var host = new GroupRelay(fixture);
            host.Install();
            var created = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new()
            {
                Name = "group",
                MemberCapacity = 10,
                InvitePolicy = GroupInvitePolicy.Administrators
            }, Token);
            close = () => fixture.Client.GroupManager.CloseGroupAsync(created.Ref, Token);
            fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "group.close" ? OfflineRelay.Error("forbidden") : host.Respond(request));
        }
        var table = channel ? "ChannelOperations" : "GroupOperations";
        await using var fault = failCleanup ? await fixture.Database.CreateTriggerAsync("fail_cleanup",
            $"BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT, 'cleanup failed'); END;") : null;
        var error = await Record.ExceptionAsync(close);
        if (!failCleanup) Assert.Equal("forbidden", Assert.IsType<RelayException>(error).Error.Code);
        else
        {
            var failures = Assert.IsType<AggregateException>(error).Flatten().InnerExceptions;
            Assert.Equal(2, failures.Count);
            Assert.Contains(failures, failure => failure is RelayException { Error.Code: "forbidden" });
            Assert.Contains(failures, failure => failure is DbUpdateException { InnerException.Message: var message } && message.Contains("cleanup failed", StringComparison.Ordinal));
        }
        await using var read = fixture.Database.Open();
        Assert.Equal(failCleanup, channel
            ? await read.ChannelOperations.AnyAsync(row => row.Method == "channel.close", Token)
            : await read.GroupOperations.AnyAsync(row => row.Method == "group.close", Token));
    }

    [Fact]
    public async Task Queued_send_retains_both_claim_and_retry_write_failures()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var claimFault = await fixture.Database.CreateTriggerAsync("fail_claim",
            $"BEFORE UPDATE ON MessageOutbox WHEN NEW.State = {(int)MessageSendState.Submitting} BEGIN SELECT RAISE(ABORT, 'claim failed'); END;");
        await using var retryFault = await fixture.Database.CreateTriggerAsync("fail_retry",
            "BEFORE UPDATE ON MessageOutbox WHEN NEW.ErrorMessage IS NOT NULL BEGIN SELECT RAISE(ABORT, 'retry save failed'); END;");
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = fixture.Client.MessageManager;
        EventHandler<BackgroundErrorEventArgs> handler = (_, args) => reported.TrySetResult(args.Error);
        manager.BackgroundError += handler;
        var completed = false;
        try
        {
            await manager.StartAsync(Token);
            var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var failures = Assert.IsType<AggregateException>(error).Flatten().InnerExceptions;
            Assert.Equal(2, failures.Count);
            Assert.Contains(failures, failure => failure is DbUpdateException { InnerException.Message: var message } && message.Contains("claim failed", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure is DbUpdateException { InnerException.Message: var message } && message.Contains("retry save failed", StringComparison.Ordinal));
            // Stop may cancel another claim whose uncancellable retry write also fails.
            var stopError = await Record.ExceptionAsync(() => manager.StopAsync(Token));
            if (stopError is not null)
            {
                var stopFailures = stopError is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [stopError];
                Assert.Contains(stopFailures, failure => failure is DbUpdateException { InnerException.Message: var message } && message.Contains("retry save failed", StringComparison.Ordinal));
                Assert.All(stopFailures, failure =>
                {
                    var writeError = Assert.IsType<DbUpdateException>(failure);
                    Assert.NotNull(writeError.InnerException);
                    Assert.True(writeError.InnerException.Message.Contains("claim failed", StringComparison.Ordinal)
                        || writeError.InnerException.Message.Contains("retry save failed", StringComparison.Ordinal));
                });
            }
            completed = true;
        }
        finally
        {
            manager.BackgroundError -= handler;
            await claimFault.DisposeAsync();
            await retryFault.DisposeAsync();
            if (!completed) _ = await Record.ExceptionAsync(() => manager.StopAsync(Token));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Send_observer_and_outcome_write_failures_both_reach_stop(bool failWrite, bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var connection = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var connected = AsyncTest.Signal();
        connection.SocketConnected += (_, _) => connected.TrySetResult();
        connection.StartNotifications();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await using var fault = failWrite ? await fixture.Database.CreateTriggerAsync("fail_retry",
            "BEFORE UPDATE ON MessageOutbox WHEN NEW.ErrorMessage = 'relay observer failed' BEGIN SELECT RAISE(ABORT, 'retry save failed'); END;") : null;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var notified = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "message.send")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                throw new HttpRequestException("send offline");
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.MessageManager;
        Exception observerError = cancellationError ? new OperationCanceledException("relay observer failed") : new IOException("relay observer failed");
        Task? stopping = null;
        var observed = 0;
        var completed = false;
        EventHandler<RelayClient> handler = (_, relay) =>
        {
            if (relay.LastError is not HttpRequestException { Message: "send offline" }
                || Interlocked.CompareExchange(ref observed, 1, 0) != 0) return;
            stopping = manager.StopAsync(Token);
            notified.TrySetResult();
            throw observerError;
        };
        try
        {
            await manager.StartAsync(Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).State == ResourceSyncState.CaughtUp);
            fixture.Pool.RelayChanged += handler;
            release.TrySetResult();
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var error = await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.NotNull(error);
            var failures = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [error];
            if (failWrite) Assert.Contains(failures, failure => failure is DbUpdateException { InnerException.Message: var message } && message.Contains("retry save failed", StringComparison.Ordinal));
            Assert.Contains(observerError, failures);
            await manager.StopAsync(Token);
            completed = true;
        }
        finally
        {
            fixture.Pool.RelayChanged -= handler;
            release.TrySetResult();
            if (fault is not null) await fault.DisposeAsync();
            if (stopping is not null) _ = await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(10), Token));
            if (!completed) _ = await Record.ExceptionAsync(() => manager.StopAsync(Token));
        }
    }
}
