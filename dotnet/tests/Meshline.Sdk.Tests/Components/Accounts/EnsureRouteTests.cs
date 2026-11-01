using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace Meshline.Tests.Components.Accounts;

public sealed class EnsureRouteTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreground_ensure_notifies_once_and_can_publish_again_from_its_callback(bool publishAgain)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        var manager = fixture.Client.AccountManager;
        var original = manager.Route!;
        var changes = new List<AccountRoute>();
        AccountRoute? callbackRoute = null;
        manager.AccountChanged += (_, _) =>
        {
            if (manager.State != AccountState.Established || manager.Route!.Revision <= original.Revision) return;
            changes.Add(manager.Route);
            if (publishAgain && changes.Count == 1)
                callbackRoute = manager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(8), cancellationToken: Token)
                    .WaitAsync(TimeSpan.FromSeconds(5), Token).GetAwaiter().GetResult();
        };

        var result = await manager.EnsureRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), Token);

        Assert.Equal(publishAgain ? 2 : 1, changes.Count);
        Assert.Equal(original.Revision + 1, changes[0].Revision);
        Assert.Equal(manager.Route!.ToJson(), result.ToJson());
        if (publishAgain) Assert.Equal(callbackRoute!.ToJson(), result.ToJson());
        await using var database = fixture.Database.Open();
        Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).Pending);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Background_route_retry_raises_account_changed_after_ensure_failure(bool reopen, bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new TimeoutException("route publication outcome is unknown");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        var failure = await Assert.ThrowsAsync<AccountRouteException>(() => fixture.Client.AccountManager.EnsureRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(7), Token));
        Assert.Equal(AccountRouteFailure.SubmissionUnknown, failure.Failure);
        string requestJson;
        await using (var database = fixture.Database.Open())
            requestJson = (await database.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).DocumentJson!;

        if (reopen) await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        var manager = fixture.Client.AccountManager;
        var changes = new ConcurrentQueue<AccountRoute>();
        manager.AccountChanged += (_, _) => changes.Enqueue(manager.Route!);
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(async () =>
        {
            await using var database = fixture.Database.Open();
            return !(await database.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).Pending;
        });
        // Drain the publisher as well as its caller before checking notifications.
        await fixture.Client.DisposeAsync();

        Assert.Equal(requestJson, Assert.Single(fixture.Relay.Requests, value => value.Method == "account.route.publish").Body);
        Assert.Equal(manager.Route!.ToJson(), Assert.Single(changes).ToJson());
    }

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
    public async Task Concurrent_ensures_take_over_an_unfinished_publication_without_waiting_for_its_response()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
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
        var publishing = fixture.Client.AccountManager.EnsureRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var route = await fixture.Client.AccountManager.EnsureRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(8), Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => publishing);
            Assert.Equal((long)TimeSpan.FromDays(8).TotalSeconds, route.ExpiresAt - route.UpdatedAt);
            Assert.Equal(fixture.Client.Route!.ToJson(), route.ToJson());
            Assert.Equal(2, publications);
        }
        finally { release.TrySetResult(); }
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
