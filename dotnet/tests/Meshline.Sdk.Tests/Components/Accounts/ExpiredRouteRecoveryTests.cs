using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class ExpiredRouteRecoveryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("save")]
    [InlineData("unknown")]
    [InlineData("accepted")]
    public async Task Interrupted_successor_is_persisted_and_replayed_or_discovered(string boundary)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.publish"
            ? throw new HttpRequestException("first route unknown") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromMinutes(5) }, Token));
        var original = Assert.Single(fixture.Relay.Requests, value => value.Method == "account.route.publish").Body;
        var device = fixture.Client.Device!.ToJson();
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(11));
        fixture.Relay.Requests.Clear();
        if (boundary == "save")
        {
            fixture.Relay.Handler = null;
            await using var db = fixture.Database.Open();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_route_successor BEFORE UPDATE OF DocumentJson ON SignedRequests WHEN NEW.Method = 'account.route.publish' BEGIN SELECT RAISE(ABORT, 'interrupted'); END;", Token);
            await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(original, (await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).DocumentJson);
            Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "account.route.publish");
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_route_successor;", Token);
        }
        else
        {
            fixture.Relay.Handler = (request, _) =>
            {
                if (request.Method == "account.route.publish")
                {
                    if (boundary == "accepted") fixture.Relay.Respond(request).Dispose();
                    throw new HttpRequestException("successor unknown");
                }
                return Task.FromResult(fixture.Relay.Respond(request));
            };
            var failure = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, failure.Reason);
        }
        string saved;
        await using (var db = fixture.Database.Open())
            saved = (await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).DocumentJson;
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.Equal(2, fixture.Client.Route!.Revision);
        Assert.Equal(fixture.Relay.RelayId, fixture.Client.Route.RelayId);
        if (boundary == "accepted") Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "account.route.publish");
        else if (boundary == "unknown") Assert.Equal(saved, Assert.Single(fixture.Relay.Requests, value => value.Method == "account.route.publish").Body);
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Fact]
    public async Task Rejected_successor_does_not_lose_the_unknown_predecessors_revision_floor()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Relay.Handler = (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            if (request.Method == "account.route.publish")
            {
                response.Dispose();
                throw new HttpRequestException("accepted first route, response lost");
            }
            return Task.FromResult(response);
        };
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromMinutes(5) }, Token));
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "account.route.publish"
            ? OfflineRelay.Error("state_conflict") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        await using (var db = fixture.Database.Open())
        {
            var rejected = await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
            Assert.Equal(2, rejected.Revision);
            Assert.False(rejected.Pending);
        }
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(3, fixture.Client.Route!.Revision);
        Assert.Single(fixture.Client.DeviceState!.Certificates);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Expired_bootstrap_cannot_move_an_undiscoverable_account_to_another_relay(bool unknown, bool directEnsure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var originalRelay = new OfflineRelay("original.test");
        var oldDevice = originalRelay.AddPeer(fixture.Account);
        var originalRoute = originalRelay.Routes[fixture.Account.AccountId];
        fixture.Relay.Routes[fixture.Account.AccountId] = originalRoute;
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            if (request.Method == "account.route.publish" && unknown)
            {
                response.Dispose();
                throw new HttpRequestException("bootstrap outcome unknown");
            }
            // Match a real non-home relay: the initial state is only staged.
            if (request.Method == "device.state.publish" && response.IsSuccessStatusCode)
            {
                response.Dispose();
                return Task.FromResult(OfflineRelay.Json(new DeviceStatePublishResponse
                {
                    Status = DeviceStatePublishStatus.Staged,
                    StagedUntil = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds() + 600
                }));
            }
            return Task.FromResult(response);
        };
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromMinutes(1) }, Token));
        var originalRequest = Assert.Single(fixture.Relay.Requests, value => value.Method == "account.route.publish").Body;
        var device = fixture.Client.Device!.ToJson();
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (directEnsure)
            {
                var error = await Assert.ThrowsAsync<Meshline.Components.AccountRouteException>(() =>
                    fixture.Client.AccountManager.EnsureRouteAsync(fixture.Relay.RelayId, TimeSpan.FromMinutes(1), Token));
                Assert.Equal(Meshline.Components.AccountRouteFailure.StateConflict, error.Failure);
            }
            else
            {
                var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
                Assert.Equal(AccountEstablishmentReason.StateConflict, error.Reason);
            }
            await fixture.ReopenAsync();
        }

        Assert.Equal(originalRoute.ToJson(), fixture.Relay.Routes[fixture.Account.AccountId].ToJson());
        Assert.Equal(oldDevice.Certificate.ToJson(), Assert.Single(originalRelay.Devices[fixture.Account.AccountId].Certificates).ToJson());
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method is "account.route.publish" or "profile.publish");
        await using var verify = fixture.Database.Open();
        Assert.Equal(originalRequest, (await verify.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).DocumentJson);
        Assert.Single(await verify.AccountEstablishments.ToListAsync(Token));
    }
}
