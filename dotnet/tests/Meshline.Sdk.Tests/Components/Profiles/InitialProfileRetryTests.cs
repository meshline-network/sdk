using Meshline.Components;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.Profiles;

public sealed class InitialProfileRetryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Retry_without_field_changes_preserves_creation_only_intent(bool usePublish, bool reopen, bool remoteExists)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "initial nickname", Bio = "initial biography", PublicDiscovery = false }
        }, Token));
        Assert.Equal(ProfileUpdateStatus.Failed, (await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).Status);

        var remote = new AccountProfile
        {
            Account = fixture.Account.AccountId,
            Nickname = "existing nickname",
            Bio = "existing biography",
            PublicDiscovery = true,
            UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds() + 10,
            DeviceSignature = []
        };
        remote = remote with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(remote.GetSigningInput(TestNetwork.Context), Token)] };
        if (remoteExists)
            fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = remote, SignerCertificate = fixture.Client.Device! };
        fixture.Relay.Handler = null;
        if (reopen) await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        var manager = fixture.Client.ProfileManager;
        var result = usePublish
            ? await manager.PublishProfileAsync(cancellationToken: Token)
            : await manager.UpdateProfileAsync(new(), Token);

        Assert.NotNull(result);
        if (remoteExists)
        {
            Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "profile.publish");
            Assert.Equal(remote.ToJson(), result.ToJson());
        }
        else
        {
            Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish");
            Assert.Equal("initial nickname", result.Nickname);
            Assert.Equal("initial biography", result.Bio);
            Assert.False(result.PublicDiscovery);
        }
        Assert.Equal(result.ToJson(), fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson());
        Assert.Equal(result.ToJson(), manager.Profile!.ToJson());
        var state = await manager.GetUpdateStateAsync(Token);
        Assert.Equal(ProfileUpdateStatus.Synchronized, state.Status);
        Assert.Null(state.PendingUpdate);
        Assert.Null(state.LastError);
        Assert.Null(state.NextAttemptAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_field_changes_supersede_creation_only_intent_after_restart(bool delete)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "initial nickname" }
        }, Token));
        var remote = new AccountProfile
        {
            Account = fixture.Account.AccountId,
            Nickname = "existing nickname",
            Bio = "existing biography",
            PublicDiscovery = true,
            UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds() + 10,
            DeviceSignature = []
        };
        remote = remote with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(remote.GetSigningInput(TestNetwork.Context), Token)] };
        fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = remote, SignerCertificate = fixture.Client.Device! };
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        var result = await fixture.Client.ProfileManager.UpdateProfileAsync(new()
        {
            Nickname = delete ? FieldUpdate<string>.Delete : "user nickname"
        }, Token);

        Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish");
        Assert.Equal(delete ? null : "user nickname", result.Nickname);
        Assert.Equal("existing biography", result.Bio);
        Assert.Equal(result.ToJson(), fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson());
        Assert.Equal(result.ToJson(), fixture.Client.Profile!.ToJson());
        Assert.Equal(ProfileUpdateStatus.Synchronized, (await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).Status);
    }
}
