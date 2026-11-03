using Meshline.Components;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Tests.Components.Profiles;

public sealed class ProfileUpdateStateTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Republication_snapshots_do_not_become_field_changes_during_publication_or_restart(bool suppliedSnapshot)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var confirmed = await manager.UpdateProfileAsync(new() { Nickname = "cached", Bio = "confirmed biography", PublicDiscovery = true }, Token);
        var snapshot = suppliedSnapshot ? confirmed with { Nickname = "snapshot", UpdatedAt = confirmed.UpdatedAt + 1 } : null;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            if (request.Method == "profile.publish") throw new TimeoutException("republication response lost");
            return fixture.Relay.Respond(request);
        };
        var observed = new ConcurrentQueue<ProfileUpdateState>();
        manager.UpdateStateChanged += (_, _) => observed.Enqueue(manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult());
        var publication = manager.PublishProfileAsync(snapshot, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var publishing = await manager.GetUpdateStateAsync(Token);
            Assert.Equal(ProfileUpdateStatus.Publishing, publishing.Status);
            Assert.NotNull(publishing.PendingUpdate);
            Assert.Equal(FieldUpdate<string>.Unspecified, publishing.PendingUpdate.Nickname);
            Assert.Equal(FieldUpdate<string>.Unspecified, publishing.PendingUpdate.Bio);
            Assert.Equal(FieldUpdate<ContentReference>.Unspecified, publishing.PendingUpdate.Avatar);
            Assert.Equal(FieldUpdate<bool>.Unspecified, publishing.PendingUpdate.PublicDiscovery);
            Assert.Equal(confirmed.ToJson(), manager.Profile!.ToJson());
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<TimeoutException>(() => publication);
        await AsyncTest.UntilAsync(() => observed.Any(value => value.Status == ProfileUpdateStatus.WaitingToRetry));
        var failed = await manager.GetUpdateStateAsync(Token);
        var prepared = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        Assert.Equal(suppliedSnapshot ? "snapshot" : "cached", ProtocolModel.FromJson<AccountProfile>(prepared!)!.Nickname);
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        manager = fixture.Client.ProfileManager;
        var restored = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, restored.Status);
        Assert.Equal(failed.LastError, restored.LastError);
        Assert.Equal(failed.NextAttemptAt, restored.NextAttemptAt);
        Assert.Equal(confirmed.ToJson(), manager.Profile!.ToJson());
        foreach (var state in observed.Append(failed).Append(restored))
        {
            Assert.NotNull(state.PendingUpdate);
            Assert.Equal(FieldUpdate<string>.Unspecified, state.PendingUpdate.Nickname);
            Assert.Equal(FieldUpdate<string>.Unspecified, state.PendingUpdate.Bio);
            Assert.Equal(FieldUpdate<ContentReference>.Unspecified, state.PendingUpdate.Avatar);
            Assert.Equal(FieldUpdate<bool>.Unspecified, state.PendingUpdate.PublicDiscovery);
        }
        fixture.Relay.Requests.Clear();
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(async () => (await manager.GetUpdateStateAsync(Token)).Status == ProfileUpdateStatus.Synchronized);
        await manager.StopAsync(Token);
        Assert.Equal(prepared, Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body);
        Assert.Equal(suppliedSnapshot ? "snapshot" : "cached", manager.Profile!.Nickname);
        Assert.Null((await manager.GetUpdateStateAsync(Token)).PendingUpdate);
    }

    [Fact]
    public async Task Republication_exposes_retained_edits_without_turning_newer_remote_values_into_field_changes()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var confirmed = await manager.UpdateProfileAsync(new() { Nickname = "cached", Bio = "cached biography", PublicDiscovery = true }, Token);
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("update response lost") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "edited", Avatar = FieldUpdate<ContentReference>.Delete }, Token));
        var prepared = ProtocolModel.FromJson<AccountProfile>(fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body!)!;
        var remote = confirmed with
        {
            Nickname = "remote",
            Bio = "remote biography",
            PublicDiscovery = false,
            UpdatedAt = prepared.UpdatedAt + 1,
            DeviceSignature = []
        };
        remote = remote with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(remote.GetSigningInput(TestNetwork.Context), Token)] };
        fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = remote, SignerCertificate = fixture.Client.Device! };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var publication = manager.PublishProfileAsync(cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var publishing = await manager.GetUpdateStateAsync(Token);
            Assert.Equal(ProfileUpdateStatus.Publishing, publishing.Status);
            Assert.Equal("edited", publishing.PendingUpdate!.Nickname.Value);
            Assert.True(publishing.PendingUpdate.Avatar.IsDeleted);
            Assert.Equal(FieldUpdate<string>.Unspecified, publishing.PendingUpdate.Bio);
            Assert.Equal(FieldUpdate<bool>.Unspecified, publishing.PendingUpdate.PublicDiscovery);
            Assert.Equal(confirmed.ToJson(), manager.Profile!.ToJson());
            var sent = ProtocolModel.FromJson<AccountProfile>(fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body!)!;
            Assert.Equal("edited", sent.Nickname);
            Assert.Null(sent.Avatar);
            Assert.Equal("remote biography", sent.Bio);
            Assert.False(sent.PublicDiscovery);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("edited", (await publication)!.Nickname);
        var completed = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, completed.Status);
        Assert.Null(completed.PendingUpdate);
        Assert.Equal("remote biography", manager.Profile!.Bio);
        Assert.False(manager.Profile.PublicDiscovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_diagnostic_write_preserves_live_error_time_and_retry_policy_until_superseded(bool permanent)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_profile_error BEFORE UPDATE OF LastErrorJson ON SignedRequests WHEN NEW.Method = 'profile.publish' AND NEW.LastErrorJson IS NOT NULL BEGIN SELECT RAISE(ABORT, 'injected error persistence failure'); END", Token);
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? permanent ? Task.FromResult(OfflineRelay.Error("invalid_state")) : throw new TimeoutException("network timeout")
            : Task.FromResult(fixture.Relay.Respond(request));
        var manager = fixture.Client.ProfileManager;
        var observed = new ConcurrentQueue<ProfileUpdateState>();
        manager.UpdateStateChanged += (_, _) => observed.Enqueue(manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult());
        var failureTime = Clock.UtcNow;
        var error = await Assert.ThrowsAsync<AggregateException>(() => manager.UpdateProfileAsync(new() { Nickname = "failed", Bio = "retained" }, Token));
        if (permanent) Assert.IsType<RelayException>(error.InnerExceptions[0]);
        else Assert.IsType<TimeoutException>(error.InnerExceptions[0]);
        Assert.IsType<DbUpdateException>(error.InnerExceptions[1]);
        var expected = permanent ? ProfileUpdateStatus.Failed : ProfileUpdateStatus.WaitingToRetry;
        await AsyncTest.UntilAsync(() => observed.Any(state => state.Status == expected));
        var state = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(expected, state.Status);
        Assert.Equal(error.InnerExceptions[0].Message, state.LastError!.Message);
        Assert.Equal(permanent ? "invalid_state" : null, state.LastError.Code);
        Assert.Equal(failureTime, state.LastError.OccurredAt);
        Assert.Equal(permanent ? null : failureTime.AddSeconds(1), state.NextAttemptAt);
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(state.LastError, (await manager.GetUpdateStateAsync(Token)).LastError);
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_profile_error", Token);
        fixture.Relay.Handler = null;
        observed.Clear();
        await manager.UpdateProfileAsync(new() { Nickname = "newer" }, Token);
        await AsyncTest.UntilAsync(() => observed.Any(value => value.Status == ProfileUpdateStatus.Synchronized));
        Assert.All(observed, value => Assert.Null(value.LastError));
        Assert.Equal("retained", manager.Profile!.Bio);
        await fixture.ReopenAsync();
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, restored.Status);
        Assert.Null(restored.LastError);
    }

    [Theory]
    [InlineData("network", "read")]
    [InlineData("network", "write")]
    [InlineData("invalid_state", "read")]
    [InlineData("invalid_state", "write")]
    [InlineData("rate_limited", "read")]
    [InlineData("rate_limited", "write")]
    [InlineData("clock_skew", "read")]
    [InlineData("clock_skew", "write")]
    public async Task Failed_outcome_persistence_preserves_live_diagnostics_and_retry_policy(string code, string phase)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var failStorage = false;
        var failures = 0;
        var publications = 0;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "profile.publish") return Task.FromResult(fixture.Relay.Respond(request));
            Interlocked.Increment(ref publications);
            failStorage = true;
            if (code == "network") throw new TimeoutException("publication network failure");
            return Task.FromResult(OfflineRelay.Json(new RelayError
            {
                Code = code,
                Message = "publication rejected",
                Data = code == "rate_limited" ? ImmutableDictionary<string, JsonElement>.Empty.Add("retry_after", JsonSerializer.SerializeToElement(90)) : null
            }, System.Net.HttpStatusCode.BadRequest));
        };
        using var observer = new DatabaseObserver((name, data) =>
        {
            if (data is not CommandEventData command || command.Command.Connection?.DataSource != fixture.Database.Options.Path) return;
            if (failStorage && name.EndsWith("CommandExecuting", StringComparison.Ordinal)
                && command.Command.CommandText.Contains(phase == "read" ? "FROM \"SignedRequests\"" : "UPDATE \"SignedRequests\"", StringComparison.Ordinal))
            {
                failStorage = false; failures++;
                throw new SqliteException("outcome persistence failed", 5);
            }
        });
        var failedAt = Clock.UtcNow;
        var error = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            if (code == "clock_skew") await manager.PublishProfileAsync(manager.Profile! with { UpdatedAt = failedAt.ToUnixTimeSeconds() + 120 }, Token);
            else await manager.UpdateProfileAsync(new() { Nickname = "pending" }, Token);
        });
        if (code == "network") Assert.IsType<TimeoutException>(error.InnerExceptions[0]);
        else Assert.Equal(code, Assert.IsType<RelayException>(error.InnerExceptions[0]).Error.Code);
        Assert.Contains("outcome persistence failed", error.InnerExceptions[1].ToString(), StringComparison.Ordinal);
        Assert.Equal(1, failures);
        var state = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(code == "invalid_state" ? ProfileUpdateStatus.Failed : ProfileUpdateStatus.WaitingToRetry, state.Status);
        Assert.Equal(code == "network" ? "publication network failure" : "publication rejected", state.LastError!.Message);
        Assert.Equal(code == "network" ? null : code, state.LastError.Code);
        Assert.Equal(failedAt, state.LastError.OccurredAt);
        Assert.Equal(code == "invalid_state" ? null : failedAt.AddSeconds(code == "clock_skew" ? 121 : code == "rate_limited" ? 90 : 1), state.NextAttemptAt);
        Assert.Equal(code != "clock_skew", state.PendingUpdate!.Nickname.IsSpecified);
        await manager.StartAsync(Token);
        await manager.StopAsync(Token);
        Assert.Equal(1, publications);
        observer.Dispose(); fixture.Relay.Handler = null;
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(3));
        await manager.UpdateProfileAsync(new() { Nickname = "new" }, Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, (await manager.GetUpdateStateAsync(Token)).Status);
        await fixture.ReopenAsync();
        Assert.Equal(ProfileUpdateStatus.Synchronized, (await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejection_retained_only_in_memory_prevents_reuse_after_storage_repair(bool background)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_rejection BEFORE UPDATE OF RequestState ON SignedRequests WHEN NEW.Method = 'profile.publish' AND NEW.RequestState = 2 BEGIN SELECT RAISE(ABORT, 'rejection write failed'); END", Token);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error(background ? "rate_limited" : "invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AggregateException>(() => manager.UpdateProfileAsync(new() { Nickname = "pending" }, Token));
        var rejected = ProtocolModel.FromJson<AccountProfile>(fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body!)!;
        await using (var database = fixture.Database.Open())
        {
            Assert.Equal(Meshline.Storage.PublicationRequestState.Prepared, (await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).RequestState);
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_rejection", Token);
        }
        fixture.Relay.Handler = null;
        if (background)
        {
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
            await manager.StartAsync(Token);
            await AsyncTest.UntilAsync(async () => (await manager.GetUpdateStateAsync(Token)).Status == ProfileUpdateStatus.Synchronized);
            await manager.StopAsync(Token);
        }
        else await manager.PublishProfileAsync(cancellationToken: Token);
        var sent = ProtocolModel.FromJson<AccountProfile>(fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body!)!;
        Assert.True(sent.UpdatedAt > rejected.UpdatedAt);
        Assert.Equal("pending", manager.Profile!.Nickname);
        await fixture.ReopenAsync();
        Assert.Equal(ProfileUpdateStatus.Synchronized, (await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).Status);
    }

    [Theory]
    [InlineData("invalid_state")]
    [InlineData("rate_limited")]
    public async Task Cancellation_preserves_a_known_initial_publication_rejection_and_its_retry_hint(string code)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var rejected = false;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "profile.publish") return Task.FromResult(fixture.Relay.Respond(request));
            rejected = true;
            return Task.FromResult(OfflineRelay.Json(new RelayError
            {
                Code = code,
                Message = "publication rejected",
                Data = code == "rate_limited" ? ImmutableDictionary<string, JsonElement>.Empty.Add("retry_after", JsonSerializer.SerializeToElement(90)) : null
            }, System.Net.HttpStatusCode.BadRequest));
        };
        using var observer = new DatabaseObserver((name, data) =>
        {
            if (rejected && name.EndsWith("CommandExecuting", StringComparison.Ordinal) && data is CommandEventData command
                && command.Command.Connection?.DataSource == fixture.Database.Options.Path
                && command.Command.CommandText.Contains("FROM \"SignedRequests\"", StringComparison.Ordinal)) caller.Cancel();
        });
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.EstablishAccountAsync(new() { InitialProfile = new() { Nickname = "initial" } }, caller.Token));
        Assert.True(caller.IsCancellationRequested);
        var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(code == "invalid_state" ? ProfileUpdateStatus.Failed : ProfileUpdateStatus.WaitingToRetry, state.Status);
        Assert.Equal(code, state.LastError!.Code);
        Assert.Equal(code == "invalid_state" ? null : Clock.UtcNow.AddSeconds(90), state.NextAttemptAt);
        await fixture.ReopenAsync();
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(state.LastError, restored.LastError);
        Assert.Equal(state.NextAttemptAt, restored.NextAttemptAt);
    }

    [Fact]
    public async Task Retry_after_diagnostic_storage_repair_persists_the_latest_failure_for_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_profile_error BEFORE UPDATE OF LastErrorJson ON SignedRequests WHEN NEW.Method = 'profile.publish' AND NEW.LastErrorJson IS NOT NULL BEGIN SELECT RAISE(ABORT, 'injected error persistence failure'); END", Token);
        var message = "first failure";
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException(message) : Task.FromResult(fixture.Relay.Respond(request));
        var manager = fixture.Client.ProfileManager;
        await Assert.ThrowsAsync<AggregateException>(() => manager.UpdateProfileAsync(new() { Nickname = "pending" }, Token));
        var first = await manager.GetUpdateStateAsync(Token);
        Assert.Equal("first failure", first.LastError!.Message);
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_profile_error", Token);
        message = "second failure";
        var observed = new ConcurrentQueue<ProfileUpdateState>();
        manager.UpdateStateChanged += (_, _) => observed.Enqueue(manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult());
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(() => observed.Any(value => value.Status == ProfileUpdateStatus.WaitingToRetry && value.LastError?.Message == "second failure"));
        await manager.StopAsync(Token);
        Assert.Contains(observed, value => value.Status == ProfileUpdateStatus.Publishing && value.LastError == first.LastError);
        var second = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(first.LastError.OccurredAt.AddSeconds(1), second.LastError!.OccurredAt);
        Assert.Equal(Clock.UtcNow.AddSeconds(2), second.NextAttemptAt);
        await fixture.ReopenAsync();
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(second.LastError, restored.LastError);
        Assert.Equal(second.NextAttemptAt, restored.NextAttemptAt);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, restored.Status);
    }

    [Fact]
    public async Task Query_and_restart_preserve_relay_retry_hint_and_error_code()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Json(new RelayError
            {
                Code = "rate_limited",
                Message = "wait for quota",
                Data = ImmutableDictionary<string, JsonElement>.Empty.Add("retry_after", JsonSerializer.SerializeToElement(90))
            }, System.Net.HttpStatusCode.BadRequest)
            : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<RelayException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Bio = "waiting" }, Token));
        var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, state.Status);
        Assert.Equal("rate_limited", state.LastError!.Code);
        Assert.Equal(Clock.UtcNow.AddSeconds(90), state.NextAttemptAt);
        await fixture.ReopenAsync();
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(state.LastError, restored.LastError);
        Assert.Equal(state.NextAttemptAt, restored.NextAttemptAt);
    }

    [Fact]
    public async Task Local_query_requires_initialization_but_not_a_profile_or_running_client()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.ProfileManager.GetUpdateStateAsync(Token));
        await fixture.InitializeAsync(establish: false);
        fixture.Relay.Handler = (_, _) => throw new InvalidOperationException("Query must stay local.");
        var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, state.Status);
        Assert.Null(state.PendingUpdate);
        Assert.Null(state.LastError);
        Assert.Null(state.NextAttemptAt);
        Assert.Null(fixture.Client.Profile);
        await fixture.ReopenAsync();
        Assert.Equal(state, await fixture.Client.ProfileManager.GetUpdateStateAsync(Token));
    }

    [Fact]
    public async Task Query_exposes_merged_three_way_fields_before_signing_without_changing_confirmed_profile()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var confirmed = await manager.UpdateProfileAsync(new() { Nickname = "confirmed", Bio = "original", PublicDiscovery = true }, Token);
        var avatar = new ContentReference { Uri = "https://example.com/avatar", Hash = "sha256:" + new string('A', 43), ContentType = "image/png", Size = 10 };
        fixture.Relay.Handler = (_, _) => throw new TimeoutException("before signing");
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "pending", Avatar = avatar }, Token));
        var first = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(avatar.ToJson(), first.PendingUpdate!.Avatar.Value.ToJson());
        Assert.False(first.PendingUpdate.Bio.IsSpecified);
        Assert.False(first.PendingUpdate.Bio.IsDeleted);
        Assert.False(first.PendingUpdate.PublicDiscovery.IsSpecified);
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Bio = FieldUpdate<string>.Delete, PublicDiscovery = false }, Token));
        var latest = await manager.GetUpdateStateAsync(Token);
        Assert.Equal("pending", latest.PendingUpdate!.Nickname.Value);
        Assert.True(latest.PendingUpdate.Bio.IsDeleted);
        Assert.False(latest.PendingUpdate.Bio.IsSpecified);
        Assert.Equal(avatar.ToJson(), latest.PendingUpdate.Avatar.Value.ToJson());
        Assert.True(latest.PendingUpdate.PublicDiscovery.IsSpecified);
        Assert.False(latest.PendingUpdate.PublicDiscovery.Value);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, latest.Status);
        Assert.Equal("before signing", latest.LastError!.Message);
        Assert.Null(latest.LastError.Code);
        Assert.Equal(Clock.UtcNow, latest.LastError.OccurredAt);
        Assert.Equal(Clock.UtcNow.AddSeconds(1), latest.NextAttemptAt);
        Assert.Equal(confirmed.ToJson(), manager.Profile!.ToJson());
        latest.PendingUpdate.Nickname = "app mutation";
        Assert.Equal("pending", (await manager.GetUpdateStateAsync(Token)).PendingUpdate!.Nickname.Value);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.UpdateProfileAsync(new() { PublicDiscovery = FieldUpdate<bool>.Delete }, Token));
        var unchanged = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(latest.Status, unchanged.Status);
        Assert.Equal(latest.LastError, unchanged.LastError);
        Assert.Equal(latest.NextAttemptAt, unchanged.NextAttemptAt);
        Assert.Equal("pending", unchanged.PendingUpdate!.Nickname.Value);
        Assert.True(unchanged.PendingUpdate.Bio.IsDeleted);
        Assert.False(unchanged.PendingUpdate.PublicDiscovery.Value);
        await fixture.ReopenAsync();
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(latest.LastError, restored.LastError);
        Assert.Equal(latest.NextAttemptAt, restored.NextAttemptAt);
        Assert.Equal(latest.Status, restored.Status);
        Assert.Equal("pending", restored.PendingUpdate!.Nickname.Value);
        Assert.True(restored.PendingUpdate.Bio.IsDeleted);
        Assert.Equal(confirmed.ToJson(), fixture.Client.Profile!.ToJson());
    }

    [Fact]
    public async Task Restart_and_background_retry_notify_publishing_and_confirmation_with_safe_reentrant_reads()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));
        var failed = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        await fixture.ReopenAsync();
        var manager = fixture.Client.ProfileManager;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return fixture.Relay.Respond(request);
        };
        var observed = new ConcurrentQueue<ProfileUpdateState>();
        var observerFailures = new ConcurrentQueue<Exception>();
        manager.BackgroundError += (_, args) => observerFailures.Enqueue(args.Error);
        manager.UpdateStateChanged += (_, _) => throw new InvalidOperationException("observer failure");
        manager.UpdateStateChanged += (_, _) => observed.Enqueue(manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult());
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var publishing = await manager.GetUpdateStateAsync(Token);
            Assert.Equal(ProfileUpdateStatus.Publishing, publishing.Status);
            Assert.Equal(failed.LastError, publishing.LastError);
            Assert.Null(publishing.NextAttemptAt);
        }
        finally { release.TrySetResult(); }
        await AsyncTest.UntilAsync(() => observed.Any(state => state.Status == ProfileUpdateStatus.Synchronized));
        await manager.StopAsync(Token);
        var completed = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, completed.Status);
        Assert.Null(completed.PendingUpdate);
        Assert.Null(completed.LastError);
        Assert.Null(completed.NextAttemptAt);
        Assert.Equal("latest", manager.Profile!.Nickname);
        Assert.Contains(observed, state => state.Status == ProfileUpdateStatus.Publishing);
        Assert.NotEmpty(observerFailures);
        Assert.All(observerFailures, error => Assert.Equal("observer failure", error.Message));
        await fixture.ReopenAsync();
        Assert.Equal(completed, await fixture.Client.ProfileManager.GetUpdateStateAsync(Token));
    }

    [Fact]
    public async Task Definitive_failure_survives_restart_and_republication_resumes_retained_changes()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish" ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<RelayException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "retained", Avatar = FieldUpdate<ContentReference>.Delete }, Token));
        var failed = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Failed, failed.Status);
        Assert.Equal("invalid_state", failed.LastError!.Code);
        Assert.Null(failed.NextAttemptAt);
        await fixture.ReopenAsync();
        var manager = fixture.Client.ProfileManager;
        var restored = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(failed.LastError, restored.LastError);
        Assert.Equal(failed.Status, restored.Status);
        Assert.True(restored.PendingUpdate!.Avatar.IsDeleted);
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = null;
        await manager.StartAsync(Token);
        await manager.StopAsync(Token);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "profile.publish");
        var events = new ConcurrentQueue<ProfileUpdateState>();
        manager.UpdateStateChanged += (_, _) => events.Enqueue(manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult());
        Assert.Equal("retained", (await manager.PublishProfileAsync(cancellationToken: Token))!.Nickname);
        await AsyncTest.UntilAsync(() => events.Any(state => state.Status == ProfileUpdateStatus.Synchronized));
        Assert.Contains(events, state => state.Status == ProfileUpdateStatus.Publishing && state.PendingUpdate?.Nickname.Value == "retained");
        Assert.All(events, state => Assert.Null(state.LastError));
    }

    [Fact]
    public async Task Discovery_confirmation_clears_error_and_pending_fields_and_notifies_without_a_new_update()
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
        var manager = fixture.Client.ProfileManager;
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "accepted" }, Token));
        var confirmed = AsyncTest.Signal();
        manager.UpdateStateChanged += (_, _) =>
        {
            if (manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult().Status == ProfileUpdateStatus.Synchronized) confirmed.TrySetResult();
        };
        Assert.Equal("accepted", (await manager.RefreshProfileAsync(cancellationToken: Token))!.Nickname);
        await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var state = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, state.Status);
        Assert.Null(state.PendingUpdate);
        Assert.Null(state.LastError);
        Assert.Null(state.NextAttemptAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_superseded_success_or_failure_cannot_replace_new_error_intent_or_notifications(bool success)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var writes = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method != "profile.publish") return fixture.Relay.Respond(request);
            if (Interlocked.Increment(ref writes) != 1) throw new TimeoutException("latest failure");
            var response = success ? fixture.Relay.Respond(request) : OfflineRelay.Error("invalid_state");
            entered.TrySetResult();
            await release.Task; // Ignore cancellation like a remote operation already in flight.
            return response;
        };
        var manager = fixture.Client.ProfileManager;
        var first = manager.UpdateProfileAsync(new() { Nickname = "old", Bio = "keep" }, Token);
        ProfileUpdateState latest;
        var observed = new ConcurrentQueue<ProfileUpdateState>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "new" }, Token));
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => first);
            latest = await manager.GetUpdateStateAsync(Token);
            manager.UpdateStateChanged += (_, _) => observed.Enqueue(manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult());
        }
        finally { release.TrySetResult(); }
        await fixture.ReopenAsync(); // Drain uncancelable continuations and read the durable state.
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(latest.LastError, restored.LastError);
        Assert.Equal(latest.NextAttemptAt, restored.NextAttemptAt);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, restored.Status);
        Assert.Equal("new", restored.PendingUpdate!.Nickname.Value);
        Assert.Equal("keep", restored.PendingUpdate.Bio.Value);
        Assert.All(observed, state =>
        {
            Assert.Equal(ProfileUpdateStatus.WaitingToRetry, state.Status);
            Assert.Equal(latest.LastError, state.LastError);
            Assert.Equal(latest.NextAttemptAt, state.NextAttemptAt);
            Assert.Equal("new", state.PendingUpdate!.Nickname.Value);
            Assert.Equal("keep", state.PendingUpdate.Bio.Value);
        });
    }

    [Fact]
    public async Task Caller_cancellation_retains_queryable_intent_and_retry_diagnostics()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.resolve") { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return fixture.Relay.Respond(request);
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var update = fixture.Client.ProfileManager.UpdateProfileAsync(new() { Bio = "saved before network" }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        await fixture.ReopenAsync();
        var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal("saved before network", state.PendingUpdate!.Bio.Value);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, state.Status);
        Assert.NotNull(state.LastError);
        Assert.NotNull(state.NextAttemptAt);
    }
}
