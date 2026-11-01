using Meshline.Storage;
using Meshline.Components;
using Meshline.Models;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Profiles;

public sealed class LatestProfileTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_write_failure_keeps_the_latest_profile_retryable_across_restart(bool afterAcceptance)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "initial" }, Token);
        await using (var write = fixture.Database.Open())
        {
            var trigger = afterAcceptance
                ? "CREATE TRIGGER reject_profile BEFORE UPDATE ON AccountProfiles BEGIN SELECT RAISE(ABORT, 'injected profile storage failure'); END"
                : "CREATE TRIGGER reject_profile BEFORE UPDATE OF DocumentJson ON SignedRequests WHEN NEW.Method = 'profile.publish' BEGIN SELECT RAISE(ABORT, 'injected request storage failure'); END";
            await write.Database.ExecuteSqlRawAsync(trigger, Token);
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest", Bio = "retained" }, Token));
        await using (var read = fixture.Database.Open())
        {
            var pending = await read.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token);
            Assert.True(pending.Pending);
            Assert.Equal(Clock.UtcNow.AddSeconds(1), pending.NextAttemptAt);
            Assert.Equal("latest", System.Text.Json.Nodes.JsonNode.Parse(pending.IntentJson!)!["nickname"]!.GetValue<string>());
            await read.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_profile", Token);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var manager = fixture.Client.ProfileManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(async () =>
        {
            await using var read = fixture.Database.Open();
            return !(await read.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending;
        });
        await manager.StopAsync(Token);
        Assert.Equal("latest", manager.Profile!.Nickname);
        Assert.Equal("retained", manager.Profile.Bio);
        Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish");
    }

    [Fact]
    public async Task Profile_version_remains_monotonic_after_restart_and_clock_rollback()
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        var first = await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "old" }, Token);
        await fixture.ReopenAsync();
        using var rollback = Clock.Use(new ManualClock(clock.GetUtcNow().AddSeconds(-5)));
        var next = await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, Token);
        Assert.True(next.UpdatedAt > first.UpdatedAt);
        Assert.Equal(next.ToJson(), fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson());
    }

    [Fact]
    public async Task Clock_skew_retry_reuses_the_latest_version_across_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error("clock_skew") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<Meshline.Transport.RelayException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));
        var expected = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var manager = fixture.Client.ProfileManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(async () =>
        {
            await using var database = fixture.Database.Open();
            return (await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).NextAttemptAt > Clock.UtcNow;
        });
        Assert.Equal(expected, Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body);
        fixture.Relay.Handler = null;
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(() => manager.Profile?.Nickname == "latest");
        await manager.StopAsync(Token);
        Assert.All(fixture.Relay.Requests.Where(value => value.Method == "profile.publish"), value => Assert.Equal(expected, value.Body));
    }

    [Fact]
    public async Task Republishing_takes_over_pending_field_updates()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var writes = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish" && Interlocked.Increment(ref writes) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var first = fixture.Client.ProfileManager.UpdateProfileAsync(new() { Bio = "retained" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var result = await fixture.Client.ProfileManager.PublishProfileAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => first);
            Assert.Equal("retained", result!.Bio);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_call_replaces_a_timeout_and_merges_fields_even_if_the_old_write_was_accepted(bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var first = true;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "profile.publish" && first)
            {
                first = false;
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new TimeoutException("Lost publication result.");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "Alice", Bio = "old" }, Token));
        var result = await manager.UpdateProfileAsync(new() { Bio = FieldUpdate<string>.Delete, PublicDiscovery = true }, Token);
        Assert.Equal("Alice", result.Nickname);
        Assert.Null(result.Bio);
        Assert.True(result.PublicDiscovery);
        var writes = fixture.Relay.Requests.Where(value => value.Method == "profile.publish").TakeLast(2).ToArray();
        Assert.True(ProtocolModel.FromJson<AccountProfile>(writes[1].Body!)!.UpdatedAt > ProtocolModel.FromJson<AccountProfile>(writes[0].Body!)!.UpdatedAt);
        Assert.Equal(result.ToJson(), fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Overlapping_calls_do_not_wait_for_an_uncancelable_old_request_or_accept_its_late_result(bool acceptBeforeDelay, bool duringRead)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var processed = AsyncTest.Signal();
        var publications = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method != (duringRead ? "profile.resolve" : "profile.publish") || Interlocked.Increment(ref publications) != 1) return fixture.Relay.Respond(request);
            var response = acceptBeforeDelay ? fixture.Relay.Respond(request) : null;
            entered.TrySetResult();
            await release.Task; // Deliberately ignore cancellation, as a remote server can.
            try { return response ?? fixture.Relay.Respond(request); }
            finally { processed.TrySetResult(); }
        };
        var manager = fixture.Client.ProfileManager;
        var old = manager.UpdateProfileAsync(new() { Nickname = "old", Bio = "kept" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await manager.UpdateProfileAsync(new() { Nickname = "new" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => old);
            Assert.Equal("kept", latest.Bio);
            release.TrySetResult();
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await manager.DisposeAsync();
            Assert.Equal(latest.ToJson(), manager.Profile!.ToJson());
            Assert.Equal(latest.ToJson(), fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson());
            await using var database = fixture.Database.Open();
            Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("internal_error")]
    [InlineData("bad_gateway")]
    public async Task Restart_retries_only_the_latest_merged_state_without_another_application_update(string? code)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? code is null ? throw new TimeoutException("offline") : Task.FromResult(OfflineRelay.Error(code))
            : Task.FromResult(fixture.Relay.Respond(request));
        var expected = code is null ? typeof(TimeoutException) : typeof(RelayException);
        await Assert.ThrowsAsync(expected, () => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "old", Bio = "retained" }, Token));
        await Assert.ThrowsAsync(expected, () => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));
        var latestRequest = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        await using (var database = fixture.Database.Open())
            Assert.Equal(Clock.UtcNow.AddSeconds(1), (await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).NextAttemptAt);
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        var manager = fixture.Client.ProfileManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(() => manager.Profile?.Nickname == "latest");
        await manager.StopAsync(Token);
        Assert.Equal("retained", manager.Profile!.Bio);
        Assert.Equal(latestRequest, Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body);
    }

    [Fact]
    public async Task Invalid_successor_does_not_supersede_a_valid_waiter()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return fixture.Relay.Respond(request);
        };
        var old = fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "valid" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { PublicDiscovery = FieldUpdate<bool>.Delete }, Token));
            Assert.False(old.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("valid", (await old).Nickname);
    }

    [Fact]
    public async Task Confirmation_of_the_retained_request_cannot_discard_new_fields_before_preparation_or_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        fixture.Relay.Handler = (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            if (request.Method == "profile.publish") { response.Dispose(); throw new TimeoutException("accepted, response lost"); }
            return Task.FromResult(response);
        };
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "old", Bio = "retained" }, Token));
        var oldDocument = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        var entered = AsyncTest.Signal();
        var reads = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.resolve" && Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return fixture.Relay.Respond(request);
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var update = fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal("old", (await fixture.Client.ProfileManager.RefreshProfileAsync(cancellationToken: Token))!.Nickname);
            await using var database = fixture.Database.Open();
            var row = await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token);
            Assert.True(row.Pending);
            Assert.Equal(PublicationRequestState.Superseded, row.RequestState);
            Assert.Equal(oldDocument, row.DocumentJson);
            Assert.Equal("latest", System.Text.Json.Nodes.JsonNode.Parse(row.IntentJson!)!["nickname"]!.GetValue<string>());
        }
        finally { await cancellation.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        var manager = fixture.Client.ProfileManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(() => manager.Profile?.Nickname == "latest");
        await manager.StopAsync(Token);
        var published = ProtocolModel.FromJson<AccountProfile>(Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body!)!;
        Assert.Equal("latest", published.Nickname);
        Assert.Equal("retained", published.Bio);
    }

    [Fact]
    public async Task Superseded_rejection_survives_restart_and_cannot_reuse_the_rejected_signature()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<Meshline.Transport.RelayException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest", Bio = "retained" }, Token));
        var rejected = ProtocolModel.FromJson<AccountProfile>(fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body!)!;
        // Admit an identical field value, then interrupt before preparing its request.
        // Content equality alone must not make the rejected signature reusable.
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw new TimeoutException("discovery unavailable") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));
        await fixture.ReopenAsync();
        await using (var database = fixture.Database.Open())
        {
            var pending = await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token);
            Assert.Equal(PublicationRequestState.SupersededRejected, pending.RequestState);
            Assert.Equal(rejected.ToJson(), pending.DocumentJson);
            Assert.True(pending.Pending);
        }
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        var manager = fixture.Client.ProfileManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(() => manager.Profile?.Nickname == "latest");
        await manager.StopAsync(Token);
        var published = ProtocolModel.FromJson<AccountProfile>(Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body!)!;
        Assert.True(published.UpdatedAt > rejected.UpdatedAt);
        Assert.Equal("latest", published.Nickname);
        Assert.Equal("retained", published.Bio);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_old_response_cannot_clear_or_invalidate_a_new_failed_publication(bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var processed = AsyncTest.Signal();
        var publications = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method != "profile.publish") return fixture.Relay.Respond(request);
            if (Interlocked.Increment(ref publications) != 1) throw new TimeoutException("new response lost");
            var response = accepted ? fixture.Relay.Respond(request) : OfflineRelay.Error("invalid_state");
            entered.TrySetResult();
            await release.Task;
            processed.TrySetResult();
            return response;
        };
        var old = fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "old" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => old);
            release.TrySetResult();
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            // Drain the transport and its continuation before checking durable state.
            await fixture.Client.DisposeAsync();
            await using var database = fixture.Database.Open();
            var pending = await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token);
            Assert.True(pending.Pending);
            Assert.Equal("latest", ProtocolModel.FromJson<AccountProfile>(pending.DocumentJson!)!.Nickname);
            Assert.Equal(PublicationRequestState.Prepared, pending.RequestState);
            Assert.NotNull(pending.IntentJson);
        }
        finally { release.TrySetResult(); }
    }
}
