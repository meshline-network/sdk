using Meshline.Models;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.Profiles;

public sealed class ProfileTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

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
    public async Task Profile_unknown_result_retries_the_identical_signed_payload()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var fail = true;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "profile.publish" && fail)
                throw new HttpRequestException("Lost response");
            return Task.FromResult(fixture.Relay.Respond(request));
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "Alice" }, Token));

        var first = fixture.Relay.Requests.Single(r => r.Method == "profile.publish").Body;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "Bob" }, Token));

        fail = false;

        await fixture.ReopenAsync();
        await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "Alice" }, Token);

        Assert.All(fixture.Relay.Requests.Where(r => r.Method == "profile.publish"), r => Assert.Equal(first, r.Body));
    }
}
