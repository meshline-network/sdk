using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountRouteAuthorizationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Existing_operation_without_route_evidence_cannot_fall_back_to_bootstrap()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw new HttpRequestException("interrupted existing account setup")
            : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        var certificate = fixture.Client.Device!.ToJson();
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            operationId = setup.OperationId;
            Assert.False(setup.IsInitialState);
            // Simulate lost route evidence, retaining the unfinished operation and
            // local device. Absence of both route records is not a new account.
            db.AccountRoutes.RemoveRange(await db.AccountRoutes.ToListAsync(Token));
            db.SignedRequests.Remove(await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token));
            await db.SaveChangesAsync(Token);
        }
        fixture.Relay.Routes.Clear();
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        var retry = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, retry.Reason);
        Assert.Equal(certificate, fixture.Client.Device!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "account.route.publish" or "device.state.publish");
        await using var verify = fixture.Database.Open();
        Assert.Equal(operationId, (await verify.AccountEstablishments.SingleAsync(Token)).OperationId);
        Assert.True((await verify.AccountEstablishments.SingleAsync(Token)).DeviceAuthorizationConfirmed);
        Assert.Single(await verify.LocalDevices.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Initial_profile_retry_renews_an_expired_route_and_preserves_device_and_settings(bool interruptRenewal, bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw new HttpRequestException("interrupted initial profile")
            : Task.FromResult(fixture.Relay.Respond(request));
        var initial = new AccountInitialProfile { Nickname = "retained nickname", Bio = "retained bio", PublicDiscovery = true };
        var first = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = initial,
            RouteValidity = TimeSpan.FromSeconds(5)
        }, Token));
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, first.Reason);
        Assert.IsType<HttpRequestException>(first.InnerException);
        Assert.Equal(0, fixture.Client.Route!.Revision);
        var certificate = fixture.Client.Device!.ToJson();
        LocalDeviceRecord local;
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            local = await db.LocalDevices.SingleAsync(Token);
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            operationId = setup.OperationId;
            Assert.True(setup.IsInitialState);
            Assert.False((await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).Pending);
        }
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) =>
        {
            if (interruptRenewal && request.Method == "account.route.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new HttpRequestException("interrupted renewal");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        if (interruptRenewal)
        {
            var interrupted = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, interrupted.Reason);
            await using (var db = fixture.Database.Open())
            {
                Assert.Equal(operationId, (await db.AccountEstablishments.SingleAsync(Token)).OperationId);
                var pending = await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
                Assert.True(pending.Pending);
                Assert.Equal(1, pending.Revision);
            }
            fixture.Relay.Handler = null;
            await fixture.ReopenAsync();
        }

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(1, fixture.Client.Route!.Revision);
        Assert.Equal(fixture.Relay.RelayId, fixture.Client.Route.RelayId);
        Assert.Equal(5, fixture.Client.Route.ExpiresAt - fixture.Client.Route.UpdatedAt);
        Assert.Equal(certificate, fixture.Client.Device!.ToJson());
        Assert.Equal(certificate, Assert.Single(fixture.Client.DeviceState!.Certificates).ToJson());
        Assert.Equal(initial.Nickname, fixture.Client.Profile!.Nickname);
        Assert.Equal(initial.Bio, fixture.Client.Profile.Bio);
        Assert.True(fixture.Client.Profile.PublicDiscovery);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        var publications = fixture.Relay.Requests.Where(request => request.Method == "account.route.publish").ToArray();
        Assert.Equal(interruptRenewal && !accepted ? 2 : 1, publications.Length);
        Assert.All(publications, request => Assert.Equal(publications[0].Body, request.Body));
        await using var verify = fixture.Database.Open();
        var retained = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(local.ProtectedSigningKey, retained.ProtectedSigningKey);
        Assert.Equal(local.ProtectedEncryptionKey, retained.ProtectedEncryptionKey);
        Assert.Equal(operationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).Pending);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_retry_of_rejected_route_reuses_its_document_without_advancing_revision(bool initialAccount)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        if (!initialAccount)
        {
            await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        }
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "account.route.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));

        var first = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, first.Reason);
        SignedRequestRecord rejected;
        LocalDeviceRecord local;
        await using (var db = fixture.Database.Open())
        {
            rejected = await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
            Assert.False(rejected.Pending);
            Assert.Equal(initialAccount ? 0 : 1, rejected.Revision);
            Assert.Equal(initialAccount ? 0 : 1, await db.AccountRoutes.CountAsync(Token));
            local = await db.LocalDevices.SingleAsync(Token);
        }
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(rejected.Revision, fixture.Client.Route!.Revision);
        var publications = fixture.Relay.Requests.Where(request => request.Method == "account.route.publish").ToArray();
        Assert.Equal(2, publications.Length);
        Assert.All(publications, request => Assert.Equal(rejected.DocumentJson, request.Body));
        await using var verify = fixture.Database.Open();
        Assert.Equal(rejected.DocumentJson, (await verify.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).DocumentJson);
        Assert.Equal(local.ProtectedSigningKey, (await verify.LocalDevices.SingleAsync(Token)).ProtectedSigningKey);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_rejected_route_requires_verified_original_route_before_resigning(bool initialAccount)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        if (!initialAccount)
        {
            await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        }
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "account.route.publish"
            ? OfflineRelay.Error("state_conflict") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            RouteValidity = TimeSpan.FromSeconds(10)
        }, Token));
        SignedRequestRecord rejected;
        LocalDeviceRecord local;
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            rejected = await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
            Assert.False(rejected.Pending);
            local = await db.LocalDevices.SingleAsync(Token);
            operationId = (await db.AccountEstablishments.SingleAsync(Token)).OperationId;
        }
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.resolve"
            ? throw new HttpRequestException("discovery unavailable") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "account.route.publish");
        await using (var db = fixture.Database.Open())
            Assert.Equal(rejected.DocumentJson, (await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).DocumentJson);

        if (initialAccount)
        {
            fixture.Relay.Handler = null;
            var blocked = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.StateConflict, blocked.Reason);
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "account.route.publish");
            await using var preserved = fixture.Database.Open();
            Assert.Equal(rejected.DocumentJson, (await preserved.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).DocumentJson);
            Assert.Equal(operationId, (await preserved.AccountEstablishments.SingleAsync(Token)).OperationId);
            return;
        }

        // The renewed document's outcome is unknown. Restart must replay that
        // exact document, not the expired rejection or a third signed request.
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.publish"
            ? throw new HttpRequestException("renewed submission unknown") : Task.FromResult(fixture.Relay.Respond(request));
        var interrupted = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, interrupted.Reason);
        var renewedJson = Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish").Body;
        var renewed = ProtocolModel.FromJson<AccountRoute>(renewedJson!)!;
        Assert.NotEqual(rejected.DocumentJson, renewedJson);
        Assert.Equal(rejected.Revision + 1, renewed.Revision);
        Assert.True(renewed.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds());
        await using (var db = fixture.Database.Open())
        {
            Assert.True((await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).Pending);
            Assert.Equal(operationId, (await db.AccountEstablishments.SingleAsync(Token)).OperationId);
        }
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(renewed.Revision, fixture.Client.Route!.Revision);
        var publications = fixture.Relay.Requests.Where(request => request.Method == "account.route.publish").ToArray();
        Assert.Equal(2, publications.Length);
        Assert.All(publications, request => Assert.Equal(renewedJson, request.Body));
        await using var verify = fixture.Database.Open();
        var retained = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(local.ProtectedSigningKey, retained.ProtectedSigningKey);
        Assert.Equal(local.ProtectedEncryptionKey, retained.ProtectedEncryptionKey);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Expired_unknown_route_requires_verified_original_route_before_superseding(bool initialAccount, bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        if (!initialAccount)
        {
            await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        }
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new HttpRequestException("unknown submission");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        var first = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            RouteValidity = TimeSpan.FromSeconds(1)
        }, Token));
        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, first.Reason);
        var original = Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish").Body;
        var originalRoute = ProtocolModel.FromJson<AccountRoute>(original!)!;
        var device = fixture.Client.Device!.ToJson();
        Guid? activationId;
        await using (var db = fixture.Database.Open())
            activationId = (await db.DevicePreparations.SingleAsync(Token)).ActivatedOperationId;
        // The initial relay staging interval can also expire before explicit retry.
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(700));
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();

        if (initialAccount)
        {
            var blocked = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.StateConflict, blocked.Reason);
            Assert.Equal(device, fixture.Client.Device!.ToJson());
            Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish");
            await using var preserved = fixture.Database.Open();
            var request = await preserved.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
            Assert.Equal(original, request.DocumentJson);
            Assert.True(request.Pending);
            Assert.Single(await preserved.AccountEstablishments.ToListAsync(Token));
            return;
        }

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(originalRoute.Revision + 1, fixture.Client.Route!.Revision);
        Assert.Equal(originalRoute.RelayId, fixture.Client.Route.RelayId);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.Equal(2, fixture.Relay.Requests.Count(request => request.Method == "account.route.publish"));
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        await using var verify = fixture.Database.Open();
        var pending = await verify.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
        Assert.False(pending.Pending);
        Assert.Equal(originalRoute.Revision + 1, pending.Revision);
        Assert.Single(await verify.LocalDevices.ToListAsync(Token));
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Equal(activationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_route_uses_its_saved_revision_and_rejects_different_validity(bool differentValidity)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.publish"
            ? throw new HttpRequestException("unknown explicit publication")
            : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.AccountManager.PublishRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(19), revision: 7, cancellationToken: Token));
        var original = Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish").Body;
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        var establishment = fixture.Client.EstablishAccountAsync(new()
        {
            RouteValidity = TimeSpan.FromDays(differentValidity ? 20 : 19)
        }, Token);

        if (differentValidity)
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => establishment);
            Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, error.Reason);
            Assert.Single(fixture.Relay.Requests, request => request.Method == "account.route.publish");
            await using (var db = fixture.Database.Open())
            {
                Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
                Assert.Empty(await db.LocalDevices.ToListAsync(Token));
                Assert.Empty(await db.DevicePreparations.ToListAsync(Token));
            }
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
            await fixture.ReopenAsync();
            await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromDays(19) }, Token);
        }
        else
        {
            await establishment;
            Assert.Equal(7, fixture.Client.Route!.Revision);
            Assert.Equal(2, fixture.Relay.Requests.Count(request => request.Method == "account.route.publish"));
            Assert.All(fixture.Relay.Requests.Where(request => request.Method == "account.route.publish"), request => Assert.Equal(original, request.Body));
        }
        await using var verify = fixture.Database.Open();
        var publication = await verify.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
        Assert.False(publication.Pending);
        Assert.Equal(original, publication.DocumentJson);
        Assert.Equal(7, fixture.Client.Route!.Revision);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_route_relay_is_retained_before_setup_and_conflicting_hints_can_be_corrected(bool omitHint)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        using var other = new OfflineRelay("pending-relay.test");
        fixture.Relay.LinkTo(other);
        other.Handler = (request, _) => request.Method == "account.route.publish"
            ? throw new HttpRequestException("unknown original route publication") : Task.FromResult(other.Respond(request));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.AccountManager.PublishRouteAsync(
            other.RelayId, TimeSpan.FromDays(19), revision: 7, cancellationToken: Token));
        var original = Assert.Single(other.Requests, value => value.Method == "account.route.publish").Body;
        other.Handler = null;
        fixture.Relay.Requests.Clear();
        if (!omitHint)
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(
                new() { RelayId = fixture.Relay.RelayId, RouteValidity = TimeSpan.FromDays(19) }, Token));
            Assert.Equal(AccountEstablishmentReason.RelayMismatch, error.Reason);
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
            Assert.DoesNotContain(other.Requests, request => request.Method == "device.state.publish");
            await using var db = fixture.Database.Open();
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
            Assert.Empty(await db.LocalDevices.ToListAsync(Token));
            Assert.Empty(await db.DevicePreparations.ToListAsync(Token));
        }
        await fixture.ReopenAsync();

        await fixture.Client.EstablishAccountAsync(new()
        {
            RelayId = omitHint ? null : other.RelayId,
            RouteValidity = TimeSpan.FromDays(19)
        }, Token);

        Assert.Equal(other.RelayId, fixture.Client.Route!.RelayId);
        Assert.Equal(7, fixture.Client.Route.Revision);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method.EndsWith(".publish", StringComparison.Ordinal));
        Assert.All(other.Requests.Where(value => value.Method == "account.route.publish"), value => Assert.Equal(original, value.Body));
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }
}
