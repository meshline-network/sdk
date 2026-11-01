using Meshline.Models;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.Profiles;

public sealed class ProfileTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_profiles_survive_restart_and_late_refresh_results(bool fail)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        var previous = await manager.UpdateProfileAsync(new() { Nickname = "before" }, Token);
        var count = fixture.Relay.Requests.Count(request => request.Method == "profile.resolve");
        for (var i = 0; i < 5; i++)
            Assert.Equal("before", (await manager.GetProfileAsync(cancellationToken: Token))!.Nickname);
        await fixture.ReopenAsync();
        manager = fixture.Client.ProfileManager;
        Assert.Equal(count, fixture.Relay.Requests.Count(request => request.Method == "profile.resolve"));
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var queries = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.resolve" && Interlocked.Increment(ref queries) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                if (fail) throw new HttpRequestException("offline");
                return OfflineRelay.Json(new Meshline.Models.Protocol.ProfileResolveResult { Profile = previous, SignerCertificate = fixture.Client.Device! });
            }
            return fixture.Relay.Respond(request);
        };
        Assert.Equal("before", (await manager.GetProfileAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token))!.Nickname);
        var refresh = manager.RefreshProfileAsync(cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var published = await manager.UpdateProfileAsync(new() { Nickname = "after" }, Token);
            Assert.True(published.UpdatedAt > previous.UpdatedAt);
            Assert.Equal("after", (await manager.GetProfileAsync(cancellationToken: Token))!.Nickname);
        }
        finally { release.TrySetResult(); }
        if (fail) await Assert.ThrowsAsync<HttpRequestException>(() => refresh);
        else Assert.Equal("after", (await refresh)!.Nickname);
        var completed = queries; // Publication retains its separate latest-state check.
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("after", (await manager.GetProfileAsync(cancellationToken: Token))!.Nickname);
        Assert.Equal(completed, queries);
        await fixture.ReopenAsync();
        Assert.Equal("after", fixture.Client.Profile!.Nickname);
    }

    [Fact]
    public async Task Profile_updates_preserve_omitted_fields_and_delete_explicit_fields()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var manager = fixture.Client.ProfileManager;
        var changes = 0;
        manager.ProfileChanged += (_, _) => changes++;
        await manager.UpdateProfileAsync(new()
        {
            Nickname = "Alice",
            Bio = "Bio",
            PublicDiscovery = true
        }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        var updated = await manager.UpdateProfileAsync(new() { Bio = FieldUpdate<string>.Delete }, Token);

        Assert.Equal("Alice", updated.Nickname);
        Assert.Null(updated.Bio);
        Assert.True(updated.PublicDiscovery);
        Assert.Equal(2, changes);

        await fixture.ReopenAsync();

        Assert.Equal(updated.ToJson(), fixture.Client.Profile!.ToJson());
    }

    [Fact]
    public async Task Profile_unknown_result_is_replaced_and_the_latest_signed_payload_survives_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        fixture.Relay.Requests.Clear();

        var fail = true;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "profile.publish" && fail)
                throw new HttpRequestException("Lost response");
            return Task.FromResult(fixture.Relay.Respond(request));
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "Alice" }, Token));

        var first = fixture.Relay.Requests.Single(r => r.Method == "profile.publish").Body;

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "Bob", Bio = "retained" }, Token));
        var latest = fixture.Relay.Requests.Last(r => r.Method == "profile.publish").Body;
        Assert.NotEqual(first, latest);

        fail = false;

        await fixture.ReopenAsync();
        var result = await fixture.Client.ProfileManager.UpdateProfileAsync(new(), Token);

        Assert.Equal(latest, fixture.Relay.Requests.Last(r => r.Method == "profile.publish").Body);
        Assert.Equal("Bob", result.Nickname);
        Assert.Equal("retained", result.Bio);
    }
}
