using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class EnsureRouteTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_route_is_reused_without_network_or_signer_even_after_missing_discovery(bool missingDiscovery)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var manager = new AccountManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool);
        await manager.InitializeAsync(Token);
        var original = manager.Route!.ToJson();
        if (missingDiscovery)
        {
            fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "account.route.resolve"
                ? OfflineRelay.Error("not_found") : fixture.Relay.Respond(request));
            Assert.Null(await manager.RefreshRouteAsync(cancellationToken: Token));
        }
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (_, _) => throw new HttpRequestException("network is unavailable");

        var route = await manager.EnsureRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), Token);

        Assert.Equal(original, route.ToJson());
        Assert.Empty(fixture.Relay.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Route_ensure_reconciles_an_unknown_submission_even_with_an_older_valid_route(bool accepted, bool throughEstablishment)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var previous = fixture.Client.Route!;
        if (throughEstablishment)
        {
            // Setup dispatches route work only after recording its current step.
            await using var db = fixture.Database.Open();
            db.AccountEstablishments.Add(new()
            {
                OperationId = Guid.NewGuid(),
                RelayId = fixture.Relay.RelayId,
                Step = AccountEstablishmentStep.EnsuringRoute,
                RouteValiditySeconds = (long)TimeSpan.FromDays(7).TotalSeconds,
                PreviousLocalDeviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context)
            });
            await db.SaveChangesAsync(Token);
        }
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new HttpRequestException("unknown newer submission");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.AccountManager.PublishRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token));
        var original = Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish").Body;
        await fixture.ReopenAsync();

        if (throughEstablishment && !accepted)
        {
            var exception = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(
                new() { RouteValidity = TimeSpan.FromDays(7) }, Token));
            Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, exception.Reason);
            await using var db = fixture.Database.Open();
            Assert.Equal(AccountEstablishmentStep.EnsuringRoute, (await db.AccountEstablishments.SingleAsync(Token)).Step);
        }
        fixture.Relay.Handler = null;

        if (throughEstablishment)
            await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromDays(7) }, Token);
        else
            await fixture.Client.AccountManager.EnsureRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), Token);

        Assert.Equal(previous.Revision + 1, fixture.Client.Route!.Revision);
        var publications = fixture.Relay.Requests.Where(request => request.Method == "account.route.publish").ToArray();
        Assert.Equal(accepted ? 1 : throughEstablishment ? 3 : 2, publications.Length);
        Assert.All(publications, request => Assert.Equal(original, request.Body));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "device.state.publish" or "profile.publish");
        await using var verify = fixture.Database.Open();
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).Pending);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Fact]
    public async Task Corrected_device_precondition_can_retry_the_identical_initial_route()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        // Match the real relay's bootstrap precondition, which the generic offline
        // peer deliberately does not impose on low-level route-only tests.
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "account.route.publish"
            && !fixture.Relay.Devices.ContainsKey(fixture.Account.AccountId)
                ? OfflineRelay.Error("state_conflict") : fixture.Relay.Respond(request));

        var rejected = await Assert.ThrowsAsync<AccountRouteException>(() => fixture.Client.AccountManager.EnsureRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(7), Token));
        Assert.Equal(AccountRouteFailure.StateConflict, rejected.Failure);
        var original = Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish").Body;

        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromDays(7) }, Token);

        Assert.Equal(0, fixture.Client.Route!.Revision);
        var publications = fixture.Relay.Requests.Where(request => request.Method == "account.route.publish").ToArray();
        Assert.Equal(2, publications.Length);
        Assert.All(publications, request => Assert.Equal(original, request.Body));
        Assert.NotNull(fixture.Client.Device);
        Assert.NotNull(fixture.Client.Profile);
    }

    [Fact]
    public async Task Conflict_callback_publication_is_the_returned_route()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        var publications = 0;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.publish" && Interlocked.Increment(ref publications) == 1)
            {
                var proposed = ProtocolModel.FromJson<AccountRoute>(request.Body!)!;
                var winner = proposed with { Revision = proposed.Revision + 1, AccountSignature = [] };
                fixture.Relay.Routes[fixture.Account.AccountId] = fixture.Relay.SignRoute(winner with
                {
                    AccountSignature = [.. fixture.Account.Sign(winner.GetAccountSigningInput(TestNetwork.Context))]
                });
                return Task.FromResult(OfflineRelay.Error("state_conflict"));
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        AccountRoute? callbackRoute = null;
        var notified = 0;
        fixture.Client.AccountManager.AccountChanged += (_, _) =>
        {
            if (publications == 0 || Interlocked.Exchange(ref notified, 1) != 0) return;
            callbackRoute = fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(5), cancellationToken: Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token).GetAwaiter().GetResult();
        };

        var result = await fixture.Client.AccountManager.EnsureRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), Token);

        Assert.NotNull(callbackRoute);
        Assert.Equal(callbackRoute.ToJson(), result.ToJson());
        Assert.Equal(result.ToJson(), fixture.Client.Route!.ToJson());
        Assert.Equal(2, publications);
    }

    [Fact]
    public async Task Missing_discovery_does_not_hide_a_concurrent_confirmed_publication()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Relay.Requests.Clear();
        AccountRoute? published = null;
        fixture.Client.AccountManager.AccountChanged += (_, _) =>
        {
            if (fixture.Client.AccountManager.State != AccountState.Unknown) return;
            // Complete publication after discovery reports missing state but before
            // its caller resumes. The verified publication supersedes that result.
            published = fixture.Client.AccountManager.PublishRouteAsync(
                fixture.Relay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token).GetAwaiter().GetResult();
        };

        var route = await fixture.Client.AccountManager.EnsureRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(7), Token);

        Assert.NotNull(published);
        Assert.Equal(published.ToJson(), route.ToJson());
        Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.resolve");
        Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish");
    }

    [Fact]
    public async Task Waiting_for_publication_rechecks_route_expiry_and_serializes_concurrent_ensures()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var publications = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.publish" && Interlocked.Increment(ref publications) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfflineRelay.Error("state_conflict");
            }
            return fixture.Relay.Respond(request);
        };
        var publishing = fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var ensuring = Enumerable.Range(0, 4).Select(_ => fixture.Client.AccountManager.EnsureRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(7), Token)).ToArray();
        Assert.All(ensuring, task => Assert.False(task.IsCompleted));
        // Expire the retained route while the ensure calls wait on the gate.
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        await Assert.ThrowsAsync<RelayException>(() => publishing);
        var routes = await Task.WhenAll(ensuring).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.All(routes, route => Assert.True(route.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds()));
        Assert.All(routes, route => Assert.Equal(fixture.Client.Route!.ToJson(), route.ToJson()));
        Assert.Equal(2, publications);
    }

    [Fact]
    public async Task Renewal_query_failure_preserves_route_and_restart_retry_publishes_once()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        var original = fixture.Client.Route!;
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.resolve"
            ? throw new HttpRequestException("discovery unavailable") : Task.FromResult(fixture.Relay.Respond(request));

        var error = await Assert.ThrowsAsync<AccountRouteException>(() => fixture.Client.AccountManager.EnsureRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(7), Token));

        Assert.Equal(AccountRouteFailure.StateUnavailable, error.Failure);
        Assert.Equal(original.ToJson(), fixture.Client.Route!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "account.route.publish");

        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        var renewed = await fixture.Client.AccountManager.EnsureRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), Token);

        Assert.Equal(original.Revision + 1, renewed.Revision);
        Assert.True(renewed.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds());
        Assert.Equal(renewed.ToJson(), fixture.Client.Route!.ToJson());
        Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish");
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "device.state.publish" or "profile.publish");
    }

    [Fact]
    public async Task Invalid_relay_is_rejected_before_network_or_signing()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Requests.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.AccountManager.EnsureRouteAsync("invalid", TimeSpan.FromDays(7), Token));

        Assert.Empty(fixture.Relay.Requests);
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.SignedRequests.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unchanged_device_authorization_needs_one_remote_read(bool renewRoute)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        var certificate = fixture.Client.Device!.ToJson();
        if (renewRoute) fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(certificate, fixture.Client.Device!.ToJson());
        Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.resolve");
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        Assert.Equal(renewRoute ? 1 : 0, fixture.Relay.Requests.Count(request => request.Method == "account.route.publish"));
    }

    [Fact]
    public async Task Initial_authorization_uses_one_confirmation_read_after_route_publication()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.resolve");
        Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        Assert.Single(fixture.Client.DeviceState!.Certificates);
    }
}
