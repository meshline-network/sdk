using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountRouteRetryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Route_retry_retains_persisted_keys_and_lifetimes_and_reconciles_its_signed_request(bool accepted, bool omitOptions)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new HttpRequestException("interrupted route publication");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            CertificateValidity = TimeSpan.FromDays(7),
            RouteValidity = TimeSpan.FromDays(19)
        }, Token));

        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
        LocalDeviceRecord prepared;
        Guid operationId;
        var original = Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish").Body;
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            operationId = setup.OperationId;
            Assert.NotEqual(Guid.Empty, setup.OperationId);
            Assert.Equal(operationId, (await db.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
            Assert.Equal((long)TimeSpan.FromDays(7).TotalSeconds, setup.CertificateValiditySeconds);
            Assert.Equal((long)TimeSpan.FromDays(19).TotalSeconds, setup.RouteValiditySeconds);
            prepared = await db.LocalDevices.SingleAsync(Token);
            Assert.True((await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).Pending);
        }
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();

        await fixture.Client.EstablishAccountAsync(omitOptions ? null : new()
        {
            CertificateValidity = TimeSpan.FromDays(30),
            RouteValidity = TimeSpan.FromDays(90)
        }, Token);

        Assert.Equal(prepared.CertificateJson, fixture.Client.Device!.ToJson());
        Assert.Equal(prepared.CertificateJson, Assert.Single(fixture.Client.DeviceState!.Certificates).ToJson());
        Assert.Equal((long)TimeSpan.FromDays(19).TotalSeconds, fixture.Client.Route!.ExpiresAt - fixture.Client.Route.UpdatedAt);
        Assert.Equal(accepted ? 1 : 2, fixture.Relay.Requests.Count(request => request.Method == "account.route.publish"));
        Assert.All(fixture.Relay.Requests.Where(request => request.Method == "account.route.publish"), request => Assert.Equal(original, request.Body));
        await using var verify = fixture.Database.Open();
        var local = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(prepared.ProtectedSigningKey, local.ProtectedSigningKey);
        Assert.Equal(prepared.ProtectedEncryptionKey, local.ProtectedEncryptionKey);
        Assert.Equal(operationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).Pending);
    }

    [Theory]
    [InlineData("stale_state", true, false)]
    [InlineData("state_conflict", true, false)]
    [InlineData("state_conflict", false, false)]
    [InlineData("stale_state", true, true)]
    [InlineData("state_conflict", true, true)]
    [InlineData("state_conflict", false, true)]
    public async Task Route_conflict_requires_a_fresh_authoritative_route(string code, bool winnerAvailable, bool initialAccount)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        string? device = null;
        if (!initialAccount)
        {
            await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
            device = fixture.Client.Device!.ToJson();
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        }
        fixture.Relay.Requests.Clear();
        AccountRoute? winner = null;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.publish")
            {
                if (winnerAvailable)
                {
                    var proposed = ProtocolModel.FromJson<AccountRoute>(request.Body!)!;
                    winner = proposed with { Revision = proposed.Revision + 1, AccountSignature = [] };
                    winner = fixture.Relay.SignRoute(winner with { AccountSignature = [.. fixture.Account.Sign(winner.GetAccountSigningInput(TestNetwork.Context))] });
                    fixture.Relay.Routes[fixture.Account.AccountId] = winner;
                }
                return Task.FromResult(OfflineRelay.Error(code));
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };

        if (winnerAvailable)
        {
            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
            Assert.Equal(winner!.ToJson(), fixture.Client.Route!.ToJson());
        }
        else
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.StateConflict, error.Reason);
            await fixture.ReopenAsync();
            var retry = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.StateConflict, retry.Reason);
            await using (var db = fixture.Database.Open())
            {
                var submitted = await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
                var proposed = ProtocolModel.FromJson<AccountRoute>(submitted.DocumentJson)!;
                winner = proposed with { Revision = proposed.Revision + 1, AccountSignature = [] };
                winner = fixture.Relay.SignRoute(winner with { AccountSignature = [.. fixture.Account.Sign(winner.GetAccountSigningInput(TestNetwork.Context))] });
                fixture.Relay.Routes[fixture.Account.AccountId] = winner;
            }
            fixture.Relay.Handler = null;
            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
            Assert.Equal(winner.ToJson(), fixture.Client.Route!.ToJson());
        }

        var routePublications = fixture.Relay.Requests.Where(request => request.Method == "account.route.publish").ToArray();
        Assert.Equal(winnerAvailable ? 1 : 2, routePublications.Length);
        Assert.All(routePublications, request => Assert.Equal(routePublications[0].Body, request.Body));
        if (initialAccount)
        {
            var publications = fixture.Relay.Requests.Where(request => request.Method == "device.state.publish").ToArray();
            Assert.NotEmpty(publications);
            Assert.All(publications, request => Assert.Equal(publications[0].Body, request.Body));
            var submitted = ProtocolModel.FromJson<AccountDeviceState>(publications[0].Body!)!;
            device = Assert.Single(submitted.Certificates).ToJson();
        }
        else
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        var methods = fixture.Relay.Requests.Select(request => request.Method).ToList();
        Assert.Contains("account.route.resolve", methods.Skip(methods.IndexOf("account.route.publish") + 1));
    }

    [Fact]
    public async Task Conflict_refresh_notification_can_publish_a_newer_route_without_a_lock_cycle()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Relay.Requests.Clear();
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
        var notified = 0;
        AccountRoute? published = null;
        fixture.Client.AccountManager.AccountChanged += (_, _) =>
        {
            // Ignore the initial transition to expired/unknown; exercise the
            // notification caused by discovering the conflict's winning route.
            if (Volatile.Read(ref publications) == 0) return;
            if (Interlocked.Exchange(ref notified, 1) != 0) return;
            published = fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(5), cancellationToken: Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token).GetAwaiter().GetResult();
        };

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.NotNull(published);
        Assert.Equal(published.ToJson(), fixture.Client.Route!.ToJson());
        Assert.Equal(published.ToJson(), fixture.Relay.Routes[fixture.Account.AccountId].ToJson());
        Assert.Equal(2, publications);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
    }
}
