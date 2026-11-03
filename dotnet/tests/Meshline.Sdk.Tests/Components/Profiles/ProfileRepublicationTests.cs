using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Meshline.Tests.Components.Profiles;

public sealed class ProfileRepublicationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("unprepared", false)]
    [InlineData("unprepared", true)]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("rejected", false)]
    [InlineData("rejected", true)]
    public async Task Older_snapshot_retry_retains_newer_intent_through_another_failure_and_restart(string failure, bool reopen)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var cached = await manager.UpdateProfileAsync(new() { Nickname = "cached", Bio = "cached biography" }, Token);
        var newer = cached with { Nickname = "newer", Bio = "newer biography", UpdatedAt = cached.UpdatedAt + 10 };
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) =>
        {
            if (failure == "unprepared" && request.Method == "profile.resolve")
                throw new TimeoutException("preparation failed");
            if (request.Method == "profile.publish")
                return failure == "rejected" ? Task.FromResult(OfflineRelay.Error("invalid_state")) : throw new TimeoutException("response lost");
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        if (failure == "rejected") await Assert.ThrowsAsync<RelayException>(() => manager.PublishProfileAsync(newer, Token));
        else await Assert.ThrowsAsync<TimeoutException>(() => manager.PublishProfileAsync(newer, Token));
        if (failure == "unprepared") Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "profile.publish");
        Assert.Equal(cached.ToJson(), manager.Profile!.ToJson());
        var failed = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(failure == "rejected" ? ProfileUpdateStatus.Failed : ProfileUpdateStatus.WaitingToRetry, failed.Status);
        Assert.Equal(FieldUpdate<string>.Unspecified, failed.PendingUpdate!.Nickname);
        if (reopen) await fixture.ReopenAsync();

        // Reject the retry too, so the next restart cannot recover the snapshot
        // from a still-reusable signed request instead of its saved intent.
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<RelayException>(() => fixture.Client.ProfileManager.PublishProfileAsync(cached, Token));
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        manager = fixture.Client.ProfileManager;
        var restored = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Failed, restored.Status);
        Assert.Equal("invalid_state", restored.LastError!.Code);
        Assert.Equal(FieldUpdate<string>.Unspecified, restored.PendingUpdate!.Nickname);
        Assert.Equal(cached.ToJson(), manager.Profile!.ToJson());
        fixture.Relay.Requests.Clear();

        var result = await manager.PublishProfileAsync(cancellationToken: Token);

        Assert.Equal("newer", result!.Nickname);
        Assert.Equal("newer biography", result.Bio);
        Assert.Equal(result.ToJson(), Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body);
        Assert.Equal(result.ToJson(), fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson());
        var completed = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, completed.Status);
        Assert.Null(completed.PendingUpdate);
        Assert.Null(completed.LastError);
        Assert.Null(completed.NextAttemptAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_merge_keeps_equal_versions_and_accepts_newer_versions_without_losing_field_edits(bool newerVersion)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var cached = await manager.UpdateProfileAsync(new() { Nickname = "cached", Bio = "cached biography" }, Token);
        var retained = cached with { Nickname = "retained", Bio = "retained biography", UpdatedAt = cached.UpdatedAt + 10 };
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<RelayException>(() => manager.PublishProfileAsync(retained, Token));
        await Assert.ThrowsAsync<RelayException>(() => manager.UpdateProfileAsync(new() { Bio = FieldUpdate<string>.Delete }, Token));
        await fixture.ReopenAsync();
        manager = fixture.Client.ProfileManager;
        fixture.Relay.Handler = null;
        var incoming = retained with
        {
            Nickname = "incoming",
            Bio = "incoming biography",
            PublicDiscovery = true,
            UpdatedAt = retained.UpdatedAt + (newerVersion ? 1 : 0)
        };

        var result = await manager.PublishProfileAsync(incoming, Token);

        Assert.Equal(newerVersion ? "incoming" : "retained", result!.Nickname);
        Assert.Equal(newerVersion, result.PublicDiscovery);
        Assert.Null(result.Bio);
        Assert.Equal(result.ToJson(), fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson());
        Assert.Equal(ProfileUpdateStatus.Synchronized, (await manager.GetUpdateStateAsync(Token)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_between_republication_read_and_admission_cannot_restore_retired_initial_values(bool reopen)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "initial", Bio = "initial biography", PublicDiscovery = false }
        }, Token));
        await Assert.ThrowsAsync<RelayException>(() => fixture.Client.ProfileManager.PublishProfileAsync(cancellationToken: Token));
        var old = ProtocolModel.FromJson<AccountProfile>(fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body!)!;
        var remote = new AccountProfile
        {
            Account = fixture.Account.AccountId,
            Nickname = "existing",
            Bio = "existing biography",
            PublicDiscovery = true,
            UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds(),
            DeviceSignature = []
        };
        Assert.True(old.UpdatedAt > remote.UpdatedAt);
        remote = remote with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(remote.GetSigningInput(TestNetwork.Context), Token)] };
        fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = remote, SignerCertificate = fixture.Client.Device! };
        fixture.Relay.Handler = null;
        if (reopen) await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        var manager = fixture.Client.ProfileManager;
        var discoveries = 0;
        using (var commands = new DatabaseObserver((name, data) =>
        {
            // Complete discovery after the pending read has materialized, before
            // republication can submit it. Do not depend on private SDK locks.
            if (data is DataReaderDisposingEventData reader && reader.Command.Connection?.DataSource == fixture.Database.Options.Path
                && reader.Command.CommandText.Contains("FROM \"SignedRequests\"", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref discoveries, 1, 0) == 0)
            {
                manager.RefreshProfileAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token).GetAwaiter().GetResult();
                Assert.Equal(ProfileUpdateStatus.Synchronized, manager.GetUpdateStateAsync(Token).GetAwaiter().GetResult().Status);
                Assert.Equal(remote.ToJson(), manager.Profile!.ToJson());
            }
        }))
        {
            var result = await manager.PublishProfileAsync(cancellationToken: Token);
            Assert.Equal(1, discoveries);
            Assert.Equal("existing", result!.Nickname);
            Assert.Equal("existing biography", result.Bio);
            Assert.True(result.PublicDiscovery);
        }
        var sent = ProtocolModel.FromJson<AccountProfile>(Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body!)!;
        Assert.Equal("existing", sent.Nickname);
        Assert.Equal("existing biography", sent.Bio);
        Assert.True(sent.PublicDiscovery);
        Assert.Equal(sent.ToJson(), fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson());
        await fixture.ReopenAsync();
        Assert.Equal(sent.ToJson(), fixture.Client.Profile!.ToJson());
        var state = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, state.Status);
        Assert.Null(state.PendingUpdate);
        Assert.Null(state.LastError);
        Assert.Null(state.NextAttemptAt);
    }

    [Fact]
    public async Task Republication_without_local_profile_or_pending_intent_does_not_create_work()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        fixture.Relay.Requests.Clear();

        Assert.Null(await fixture.Client.ProfileManager.PublishProfileAsync(cancellationToken: Token));

        Assert.Empty(fixture.Relay.Requests);
        Assert.Equal(ProfileUpdateStatus.Synchronized, (await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_republication_reuses_unknown_request_after_restart(bool suppliedSnapshot)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var confirmed = await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "cached", Bio = "biography" }, Token);
        var supplied = suppliedSnapshot ? confirmed with { Nickname = "supplied", UpdatedAt = confirmed.UpdatedAt + 1 } : null;
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("response lost") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.PublishProfileAsync(supplied, Token));
        var original = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var restored = await fixture.Client.ProfileManager.GetUpdateStateAsync(Token);
        Assert.Equal(FieldUpdate<string>.Unspecified, restored.PendingUpdate!.Nickname);

        var result = await fixture.Client.ProfileManager.PublishProfileAsync(cancellationToken: Token);

        Assert.Equal(original, Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body);
        Assert.Equal(suppliedSnapshot ? "supplied" : "cached", result!.Nickname);
        Assert.Equal("biography", result.Bio);
        Assert.Equal(ProfileUpdateStatus.Synchronized, (await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).Status);
    }
}
