using Meshline.Components;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace Meshline.Tests.Components.Profiles;

public sealed class InitialProfilePublicationStateTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Discovery_retires_unprepared_initial_values_before_later_edits(bool reopen, bool refresh)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        var reads = 0;
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve" && Interlocked.Increment(ref reads) == 2
            ? throw new TimeoutException("initial preparation lookup failed") : Task.FromResult(fixture.Relay.Respond(request));
        var failure = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "initial", Bio = "initial biography", PublicDiscovery = false }
        }, Token));
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, failure.Reason);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "profile.publish");
        if (reopen) await fixture.ReopenAsync();
        var remote = new AccountProfile
        {
            Account = fixture.Account.AccountId,
            Nickname = "existing",
            Bio = "existing biography",
            PublicDiscovery = true,
            UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds() + 10,
            DeviceSignature = []
        };
        remote = remote with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(remote.GetSigningInput(TestNetwork.Context), Token)] };
        fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = remote, SignerCertificate = fixture.Client.Device! };
        fixture.Relay.Handler = null;
        var manager = fixture.Client.ProfileManager;
        var synchronized = AsyncTest.Signal();
        manager.UpdateStateChanged += (_, _) =>
        {
            if (manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult().Status == ProfileUpdateStatus.Synchronized)
                synchronized.TrySetResult();
        };
        if (refresh) await manager.RefreshProfileAsync(cancellationToken: Token);
        else await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        await synchronized.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var state = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, state.Status);
        Assert.Null(state.PendingUpdate);
        Assert.Null(state.LastError);
        Assert.Null(state.NextAttemptAt);
        Assert.Equal(remote.ToJson(), manager.Profile!.ToJson());
        var edited = await manager.UpdateProfileAsync(new() { Nickname = "user edit only" }, Token);
        Assert.Equal("existing biography", edited.Bio);
        Assert.True(edited.PublicDiscovery);
        if (refresh) await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        await using (var database = fixture.Database.Open()) Assert.Empty(await database.AccountEstablishments.ToListAsync(Token));
        await fixture.ReopenAsync();
        Assert.Equal(state, await fixture.Client.ProfileManager.GetUpdateStateAsync(Token));
        Assert.Equal(edited.ToJson(), fixture.Client.Profile!.ToJson());
    }

    [Fact]
    public async Task Discovery_during_setup_keeps_user_edits_admitted_after_the_lookup_started()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        var reads = 0;
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve" && Interlocked.Increment(ref reads) == 2
            ? throw new TimeoutException("initial preparation lookup failed") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(
            new() { InitialProfile = new() { Nickname = "initial", Bio = "initial biography" } }, Token));
        var remote = new AccountProfile
        {
            Account = fixture.Account.AccountId,
            Nickname = "existing",
            Bio = "existing biography",
            PublicDiscovery = true,
            UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds() + 10,
            DeviceSignature = []
        };
        remote = remote with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(remote.GetSigningInput(TestNetwork.Context), Token)] };
        fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = remote, SignerCertificate = fixture.Client.Device! };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        reads = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.resolve" && Interlocked.Increment(ref reads) == 1)
            {
                var response = fixture.Relay.Respond(request);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return response;
            }
            if (request.Method == "profile.publish") throw new TimeoutException("user response lost");
            return fixture.Relay.Respond(request);
        };
        var setup = fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        ProfileUpdateState pending;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new()
            {
                Nickname = "user edit",
                Bio = FieldUpdate<string>.Delete,
                PublicDiscovery = true
            }, Token));
            pending = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        }
        finally { release.TrySetResult(); }
        await setup.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(remote.ToJson(), fixture.Client.Profile!.ToJson());
        await fixture.ReopenAsync();
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, restored.Status);
        Assert.Equal(pending.LastError, restored.LastError);
        Assert.Equal(pending.NextAttemptAt, restored.NextAttemptAt);
        Assert.Equal("user edit", restored.PendingUpdate!.Nickname.Value);
        Assert.True(restored.PendingUpdate.Bio.IsDeleted);
        Assert.True(restored.PendingUpdate.PublicDiscovery.Value);
        fixture.Relay.Handler = null;
        var completed = await fixture.Client.ProfileManager.PublishProfileAsync(cancellationToken: Token);
        Assert.Equal("user edit", completed!.Nickname);
        Assert.Null(completed.Bio);
        Assert.True(completed.PublicDiscovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_fences_late_initial_results_after_retiring_creation_intent(bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method == "profile.publish")
            {
                var response = accepted ? fixture.Relay.Respond(request) : OfflineRelay.Error("invalid_state");
                entered.TrySetResult();
                await release.Task;
                return response;
            }
            return fixture.Relay.Respond(request);
        };
        var setup = fixture.Client.EstablishAccountAsync(new() { InitialProfile = new() { Nickname = "initial" } }, Token);
        var observed = new ConcurrentQueue<ProfileUpdateState>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var remote = new AccountProfile
            {
                Account = fixture.Account.AccountId,
                Nickname = "existing",
                Bio = "existing biography",
                PublicDiscovery = true,
                UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds() + 10,
                DeviceSignature = []
            };
            remote = remote with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(remote.GetSigningInput(TestNetwork.Context), Token)] };
            fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = remote, SignerCertificate = fixture.Client.Device! };
            await fixture.Client.ProfileManager.RefreshProfileAsync(cancellationToken: Token);
            Assert.Equal(ProfileUpdateStatus.Synchronized, (await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).Status);
            fixture.Client.ProfileManager.UpdateStateChanged += (_, _) => observed.Enqueue(fixture.Client.ProfileManager.GetUpdateStateAsync(Token).GetAwaiter().GetResult());
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<StateUpdateSupersededException>(() => setup);
        await fixture.ReopenAsync();
        Assert.All(observed, state => Assert.Equal(ProfileUpdateStatus.Synchronized, state.Status));
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, restored.Status);
        Assert.Null(restored.PendingUpdate);
        Assert.Null(restored.LastError);
        Assert.Null(restored.NextAttemptAt);
        Assert.Equal("existing", fixture.Client.Profile!.Nickname);
        Assert.Equal("existing biography", fixture.Client.Profile.Bio);
        Assert.True(fixture.Client.Profile.PublicDiscovery);
        fixture.Relay.Handler = null;
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
    }

    [Fact]
    public async Task Initial_submission_and_diagnostic_write_failures_preserve_unknown_outcome_and_live_state()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_profile_error BEFORE UPDATE OF LastErrorJson ON SignedRequests WHEN NEW.Method = 'profile.publish' AND NEW.LastErrorJson IS NOT NULL BEGIN SELECT RAISE(ABORT, 'injected error persistence failure'); END", Token);
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("initial response lost") : Task.FromResult(fixture.Relay.Respond(request));
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(
            new() { InitialProfile = new() { Nickname = "initial" } }, Token));
        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
        var causes = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
        Assert.IsType<TimeoutException>(causes[0]);
        Assert.IsType<DbUpdateException>(causes[1]);
        var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.WaitingToRetry, state.Status);
        Assert.Equal(causes[0].Message, state.LastError!.Message);
        Assert.Equal(Clock.UtcNow, state.LastError.OccurredAt);
        Assert.Equal(Clock.UtcNow.AddSeconds(1), state.NextAttemptAt);
        Assert.Equal("initial", state.PendingUpdate!.Nickname.Value);
        Assert.Null(fixture.Client.Profile);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("invalid_state", false)]
    [InlineData("timeout", false)]
    [InlineData("timeout", true)]
    public async Task Initial_publication_exposes_progress_outcome_and_restart_recovery(string? failure, bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        var manager = fixture.Client.ProfileManager;
        var observed = new ConcurrentQueue<ProfileUpdateState>();
        manager.UpdateStateChanged += (_, _) => observed.Enqueue(manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult());
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                if (failure == "timeout")
                {
                    if (accepted) fixture.Relay.Respond(request).Dispose();
                    throw new TimeoutException("initial response lost");
                }
                if (failure is not null) return OfflineRelay.Error(failure);
            }
            return fixture.Relay.Respond(request);
        };
        var setup = fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "initial", Bio = "retained", PublicDiscovery = true }
        }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var publishing = await manager.GetUpdateStateAsync(Token);
            Assert.Equal(ProfileUpdateStatus.Publishing, publishing.Status);
            Assert.Equal("initial", publishing.PendingUpdate!.Nickname.Value);
            Assert.Equal("retained", publishing.PendingUpdate.Bio.Value);
            Assert.True(publishing.PendingUpdate.PublicDiscovery.Value);
            Assert.Null(manager.Profile);
            Assert.Contains(observed, value => value.Status == ProfileUpdateStatus.Publishing);
        }
        finally { release.TrySetResult(); }
        if (failure is null)
        {
            await setup;
            await AsyncTest.UntilAsync(() => observed.Any(value => value.Status == ProfileUpdateStatus.Synchronized));
            Assert.Equal("initial", manager.Profile!.Nickname);
            return;
        }
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => setup);
        Assert.Equal(failure == "timeout" ? AccountEstablishmentReason.SubmissionOutcomeUnknown : AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        var expected = failure == "timeout" ? ProfileUpdateStatus.WaitingToRetry : ProfileUpdateStatus.Failed;
        await AsyncTest.UntilAsync(() => observed.Any(value => value.Status == expected));
        var failed = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(error.InnerException!.Message, failed.LastError!.Message);
        Assert.Equal(failure == "timeout" ? null : failure, failed.LastError.Code);
        Assert.Equal(Clock.UtcNow, failed.LastError.OccurredAt);
        Assert.Equal(failure == "timeout" ? Clock.UtcNow.AddSeconds(1) : null, failed.NextAttemptAt);
        var originalRequest = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        manager = fixture.Client.ProfileManager;
        var restored = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(expected, restored.Status);
        Assert.Equal(failed.LastError, restored.LastError);
        Assert.Equal(failed.NextAttemptAt, restored.NextAttemptAt);
        Assert.Equal("initial", restored.PendingUpdate!.Nickname.Value);
        fixture.Relay.Requests.Clear();
        await manager.StartAsync(Token);
        if (failure == "timeout")
        {
            await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
            await AsyncTest.UntilAsync(async () => (await manager.GetUpdateStateAsync(Token)).Status == ProfileUpdateStatus.Synchronized);
        }
        await manager.StopAsync(Token);
        if (failure != "timeout")
        {
            Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "profile.publish");
            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        }
        var completed = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, completed.Status);
        Assert.Null(completed.PendingUpdate);
        Assert.Null(completed.LastError);
        Assert.Equal("retained", manager.Profile!.Bio);
        if (failure == "timeout")
        {
            Assert.Equal(accepted ? 0 : 1, fixture.Relay.Requests.Count(value => value.Method == "profile.publish"));
            Assert.All(fixture.Relay.Requests.Where(value => value.Method == "profile.publish"), value => Assert.Equal(originalRequest, value.Body));
            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_values_do_not_replace_edits_admitted_during_the_initial_discovery(bool retryFails)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var reads = 0;
        var writes = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.resolve" && Interlocked.Increment(ref reads) == 1)
            {
                var response = fixture.Relay.Respond(request);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return response;
            }
            if (request.Method == "profile.publish" && (Interlocked.Increment(ref writes) == 1 || retryFails))
                throw new TimeoutException("user edit response lost");
            return fixture.Relay.Respond(request);
        };
        var setup = fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "initial default", Bio = "initial biography" }
        }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new()
            {
                Nickname = "user edit",
                Bio = "user biography",
                PublicDiscovery = true
            }, Token));
        }
        finally { release.TrySetResult(); }
        if (retryFails)
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => setup.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
            Assert.IsType<TimeoutException>(error.InnerException);
            var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
            Assert.Equal("user edit", state.PendingUpdate!.Nickname.Value);
            Assert.Equal(ProfileUpdateStatus.WaitingToRetry, state.Status);
            return;
        }
        await setup.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("user edit", fixture.Client.Profile!.Nickname);
        Assert.Equal("user biography", fixture.Client.Profile.Bio);
        Assert.True(fixture.Client.Profile.PublicDiscovery);
        var publications = fixture.Relay.Requests.Where(value => value.Method == "profile.publish").ToArray();
        Assert.Equal(2, publications.Length);
        Assert.Equal(publications[0].Body, publications[1].Body);
    }

    [Fact]
    public async Task Initial_creation_rechecks_remote_existence_before_publishing_defaults()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        var reads = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method == "profile.resolve" && Interlocked.Increment(ref reads) == 2)
            {
                var profile = new AccountProfile
                {
                    Account = fixture.Account.AccountId,
                    Nickname = "existing",
                    PublicDiscovery = true,
                    UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds(),
                    DeviceSignature = []
                };
                profile = profile with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(profile.GetSigningInput(TestNetwork.Context), Token)] };
                fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = profile, SignerCertificate = fixture.Client.Device! };
            }
            return fixture.Relay.Respond(request);
        };
        await fixture.Client.EstablishAccountAsync(new() { InitialProfile = new() { Nickname = "default" } }, Token);
        Assert.Equal("existing", fixture.Client.Profile!.Nickname);
        Assert.True(fixture.Client.Profile.PublicDiscovery);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "profile.publish");
        var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, state.Status);
        Assert.Null(state.PendingUpdate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_edits_fence_late_initial_publication_results(bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var writes = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method == "profile.publish" && Interlocked.Increment(ref writes) == 1)
            {
                var response = accepted ? fixture.Relay.Respond(request) : OfflineRelay.Error("invalid_state");
                entered.TrySetResult();
                await release.Task;
                return response;
            }
            return fixture.Relay.Respond(request);
        };
        var setup = fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "initial", Bio = "retained" }
        }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "newer" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal("newer", latest.Nickname);
            Assert.Equal("retained", latest.Bio);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => setup);
        }
        finally { release.TrySetResult(); }
        await fixture.ReopenAsync();
        var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, state.Status);
        Assert.Null(state.LastError);
        Assert.Equal("newer", fixture.Client.Profile!.Nickname);
        Assert.Equal("retained", fixture.Client.Profile.Bio);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal("newer", fixture.Client.Profile.Nickname);
    }
}
