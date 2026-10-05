using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Establishment_publishes_staged_devices_before_route_and_survives_reopen()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var client = fixture.Client;

        Assert.NotNull(client.Device);
        Assert.NotNull(client.Route);
        Assert.NotNull(client.DeviceState);
        Assert.Null(client.DeviceState.ValidateDeviceAuthorization(client.Device.GetDeviceId(TestNetwork.Context), TestNetwork.Context));

        var operations = fixture.Relay.Requests.Select(request => request.Method).ToList();

        Assert.True(operations.IndexOf("device.state.publish") < operations.IndexOf("account.route.publish"));

        var deviceId = client.Device.GetDeviceId(TestNetwork.Context);

        await fixture.ReopenAsync();

        Assert.Equal(deviceId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(client.Route.Revision, fixture.Client.Route!.Revision);

        await using var db = fixture.Database.Open();

        Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
    }

    [Fact]
    public async Task Failed_route_publication_reuses_identical_signed_request_after_reopen()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);

        var relay = fixture.Relay;
        var fail = true;
        relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.publish" && fail)
                throw new HttpRequestException("Lost response");
            return Task.FromResult(relay.Respond(request));
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.EstablishAccountAsync(new() { RelayId = relay.RelayId }, Token));

        var original = relay.Requests.Single(r => r.Method == "account.route.publish").Body;

        await fixture.ReopenAsync();
        fail = false;
        await fixture.Client.EstablishAccountAsync(new() { RelayId = relay.RelayId }, Token);

        Assert.All(relay.Requests.Where(r => r.Method == "account.route.publish"), request => Assert.Equal(original, request.Body));

        await using var db = fixture.Database.Open();

        Assert.False((await db.SignedRequests.SingleAsync(r => r.Method == "account.route.publish", Token)).Pending);
    }

    [Fact]
    public async Task Route_revisions_increase_and_old_route_cannot_replace_known_state()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var previous = fixture.Client.Route!;
        var next = await fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);

        Assert.True(next.Revision > previous.Revision);

        fixture.Relay.Routes[fixture.Account.AccountId] = previous;

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.AccountManager.GetRouteAsync(cancellationToken: Token));
        Assert.Equal(next.Revision, fixture.Client.Route!.Revision);
    }

    [Fact]
    public async Task Route_discovery_continues_to_another_relay_after_request_timeout()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var other = new OfflineRelay("other.test");
        fixture.Relay.LinkTo(other);
        other.Routes[fixture.Account.AccountId] = fixture.Client.Route!;
        var entered = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return fixture.Relay.Respond(request);
        };
        var pending = fixture.Client.AccountManager.GetRouteAsync(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(60));
        var route = await pending.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.NotNull(route);
        Assert.Equal(fixture.Client.Route!.Revision, route.Revision);
        Assert.Contains(other.Requests, request => request.Method == "account.route.resolve");
    }

    [Fact]
    public async Task Account_recovery_authorizes_local_device_with_newer_revision()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var previous = fixture.Client.DeviceState!.Revision;
        await fixture.Client.RecoverAccountAsync(new AccountRecoveryOptions { RelayId = fixture.Relay.RelayId }, Token);

        Assert.True(fixture.Client.DeviceState!.Revision > previous);
        Assert.Null(fixture.Client.DeviceState.ValidateDeviceAuthorization(fixture.Client.Device!.GetDeviceId(TestNetwork.Context), TestNetwork.Context));
    }
}
