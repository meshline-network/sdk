using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Tests.Components.Profiles;

public sealed class LegacyProfilePublicationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_edits_on_a_confirmed_profile_are_replayed_even_with_an_initial_workflow(bool unfinishedSetup)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "confirmed" }, Token);
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("lost update") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(
            new() { Nickname = "pending edit", Bio = "pending biography", PublicDiscovery = true }, Token));
        var request = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        if (unfinishedSetup)
        {
            await using var database = fixture.Database.Open();
            database.AccountEstablishments.Add(new()
            {
                OperationId = Guid.NewGuid(),
                RelayId = fixture.Relay.RelayId,
                Step = AccountEstablishmentStep.InitializingProfile,
                IsInitialState = true,
                DeviceAuthorizationConfirmed = true,
                InitialProfileJson = JsonSerializer.Serialize(new AccountInitialProfile
                {
                    Nickname = "pending edit",
                    Bio = "pending biography",
                    PublicDiscovery = true
                })
            });
            await database.SaveChangesAsync(Token);
        }
        await UseLegacyRequestFormatAsync(fixture);
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        var manager = fixture.Client.ProfileManager;
        Assert.Equal("pending edit", (await manager.GetUpdateStateAsync(Token)).PendingUpdate!.Nickname.Value);
        fixture.Relay.Requests.Clear();
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await manager.GetUpdateStateAsync(Token)).Status == ProfileUpdateStatus.Synchronized);
        await manager.StopAsync(Token);
        Assert.Equal(request, Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body);
        Assert.Equal("pending edit", manager.Profile!.Nickname);
        Assert.Equal("pending biography", manager.Profile.Bio);
        Assert.True(manager.Profile.PublicDiscovery);
        await fixture.ReopenAsync();
        Assert.Null((await fixture.Client.ProfileManager.GetUpdateStateAsync(Token)).PendingUpdate);
        Assert.Equal("pending edit", fixture.Client.Profile!.Nickname);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_initial_request_uses_matching_workflow_and_preserves_existing_remote_profile(bool remoteExists)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("initial response lost") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "initial", Bio = "initial biography", PublicDiscovery = true }
        }, Token));
        var request = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        await UseLegacyRequestFormatAsync(fixture);
        if (remoteExists)
        {
            var remote = ProtocolModel.FromJson<AccountProfile>(request!)! with
            {
                Nickname = "existing",
                Bio = "existing biography",
                PublicDiscovery = false,
                UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds() + 10,
                DeviceSignature = []
            };
            remote = remote with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(remote.GetSigningInput(TestNetwork.Context), Token)] };
            fixture.Relay.Profiles[fixture.Account.AccountId] = new() { Profile = remote, SignerCertificate = fixture.Client.Device! };
        }
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        var manager = fixture.Client.ProfileManager;
        Assert.Equal("initial", (await manager.GetUpdateStateAsync(Token)).PendingUpdate!.Nickname.Value);
        fixture.Relay.Requests.Clear();
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await manager.GetUpdateStateAsync(Token)).Status == ProfileUpdateStatus.Synchronized);
        await manager.StopAsync(Token);
        if (remoteExists) Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "profile.publish");
        else Assert.Equal(request, Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body);
        Assert.Equal(remoteExists ? "existing" : "initial", manager.Profile!.Nickname);
        Assert.Equal(remoteExists ? "existing biography" : "initial biography", manager.Profile.Bio);
        Assert.Equal(!remoteExists, manager.Profile.PublicDiscovery);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        await using var database = fixture.Database.Open();
        Assert.Empty(await database.AccountEstablishments.ToListAsync(Token));
    }

    [Fact]
    public async Task Legacy_user_edits_without_cached_profile_are_not_mistaken_for_initial_values()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish: false);
        fixture.Relay.Handler = (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            if (request.Method == "profile.publish")
            {
                response.Dispose();
                throw new TimeoutException("initial response lost");
            }
            return Task.FromResult(response);
        };
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(
            new() { InitialProfile = new() { Nickname = "initial" } }, Token));
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("user response lost") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "user edit" }, Token));
        Assert.Null(fixture.Client.Profile);
        var request = fixture.Relay.Requests.Last(value => value.Method == "profile.publish").Body;
        await UseLegacyRequestFormatAsync(fixture);
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var manager = fixture.Client.ProfileManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await manager.GetUpdateStateAsync(Token)).Status == ProfileUpdateStatus.Synchronized);
        await manager.StopAsync(Token);
        Assert.Equal(request, Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish").Body);
        Assert.Equal("user edit", manager.Profile!.Nickname);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal("user edit", fixture.Client.Profile!.Nickname);
    }

    static async Task UseLegacyRequestFormatAsync(TestClient fixture)
    {
        await using var database = fixture.Database.Open();
        var row = await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token);
        row.IntentJson = null;
        row.Generation = 0;
        row.RequestState = PublicationRequestState.None;
        row.NextAttemptAt = null;
        row.LastErrorJson = null;
        await database.SaveChangesAsync(Token);
    }
}
