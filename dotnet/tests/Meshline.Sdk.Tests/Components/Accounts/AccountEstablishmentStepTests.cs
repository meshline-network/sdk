using Meshline.Models.Protocol;
using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountEstablishmentStepTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route_renewal_waits_for_stopped_runtime_and_a_saved_step(bool running)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        var previousRevision = fixture.Client.Route!.Revision;
        if (running) await fixture.Client.StartAsync(Token);
        else
        {
            await using var db = fixture.Database.Open();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_setup BEFORE INSERT ON AccountEstablishment BEGIN SELECT RAISE(ABORT, 'setup interrupted'); END;", Token);
        }
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Relay.Requests.Clear();

        var options = new AccountEstablishmentOptions { RouteValidity = TimeSpan.FromDays(7) };
        if (running) await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.EstablishAccountAsync(options, Token));
        else await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(options, Token));

        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "account.route.publish");
        await using (var db = fixture.Database.Open())
        {
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
            if (!running) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_setup;", Token);
        }
        if (running) await fixture.Client.StopAsync(Token);
        await fixture.ReopenAsync();
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method == "account.route.publish")
            {
                await using var db = fixture.Database.Open();
                var setup = await db.AccountEstablishments.SingleAsync(Token);
                Assert.Equal(AccountEstablishmentStep.EnsuringRoute, setup.Step);
                Assert.Equal((long)TimeSpan.FromDays(7).TotalSeconds, setup.RouteValiditySeconds);
            }
            return fixture.Relay.Respond(request);
        };

        await fixture.Client.EstablishAccountAsync(options, Token);

        Assert.Single(fixture.Relay.Requests, value => value.Method == "account.route.publish");
        Assert.Equal(previousRevision + 1, fixture.Client.Route!.Revision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Valid_account_reuses_discovered_route_despite_an_unrelated_unknown_publication(bool accepted, bool running)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var previousRevision = fixture.Client.Route!.Revision;
        var device = fixture.Client.Device!.ToJson();
        var profile = fixture.Client.Profile!.ToJson();
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new HttpRequestException("unrelated unknown route publication");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.AccountManager.PublishRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token));
        string signed;
        await using (var db = fixture.Database.Open())
        {
            signed = (await db.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token)).DocumentJson;
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_setup BEFORE INSERT ON AccountEstablishment BEGIN SELECT RAISE(ABORT, 'unexpected setup'); END;", Token);
        }
        fixture.Relay.Handler = null;
        await fixture.Client.DisposeAsync();
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector);
        await client.InitializeAsync(Token);
        if (running) await client.StartAsync(Token);
        fixture.Relay.Requests.Clear();

        // Defaults deliberately differ from the unrelated request's lifetime.
        // Neither an account signer nor stopped background sync is needed.
        await client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(previousRevision + (accepted ? 1 : 0), client.Route!.Revision);
        Assert.Equal(device, client.Device!.ToJson());
        Assert.Equal(profile, client.Profile!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method.EndsWith(".publish", StringComparison.Ordinal) || value.Method == "auth.account.verify");
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        var publication = await verify.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", Token);
        Assert.Equal(signed, publication.DocumentJson);
        Assert.Equal(!accepted, publication.Pending);
        if (running) await client.StopAsync(Token);
    }

    [Fact]
    public async Task Valid_account_does_not_create_a_workflow_or_publish()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_setup BEFORE INSERT ON AccountEstablishment BEGIN SELECT RAISE(ABORT, 'unexpected setup'); END;", Token);
        fixture.Relay.Requests.Clear();
        var reads = 0;
        fixture.Protector.ReadFailure = _ => { reads++; return null; };

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method.EndsWith(".publish", StringComparison.Ordinal));
        Assert.Single(fixture.Relay.Requests, value => value.Method == "account.route.resolve");
        Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.resolve");
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("staged-device")]
    [InlineData("accepted-route")]
    [InlineData("confirmed-device")]
    public async Task Completed_component_work_is_replayed_when_advancing_the_step_fails(string boundary)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var nextStep = boundary switch
        {
            "staged-device" => AccountEstablishmentStep.EnsuringRoute,
            "accepted-route" => AccountEstablishmentStep.AuthorizingDevice,
            _ => AccountEstablishmentStep.InitializingProfile
        };
        // SQLite trigger definitions cannot bind parameters; the only substitution
        // is an integer selected from the fixed internal enum above.
        var trigger = $"CREATE TRIGGER fail_step BEFORE UPDATE OF Step ON AccountEstablishment WHEN NEW.Step = {(int)nextStep} BEGIN SELECT RAISE(ABORT, 'step interrupted'); END;";
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync(trigger, Token);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "Retained", Bio = "Original", PublicDiscovery = true }
        }, Token));

        LocalDeviceRecord prepared;
        string publication;
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(boundary == "accepted-route" ? AccountEstablishmentStep.EnsuringRoute : AccountEstablishmentStep.AuthorizingDevice, setup.Step);
            operationId = setup.OperationId;
            prepared = await db.LocalDevices.SingleAsync(Token);
            publication = (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson;
            Assert.Equal(operationId, (await db.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
            Assert.False(await db.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_step;", Token);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        await using var verify = fixture.Database.Open();
        var local = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(prepared.CertificateJson, local.CertificateJson);
        Assert.Equal(prepared.ProtectedSigningKey, local.ProtectedSigningKey);
        Assert.Equal(prepared.ProtectedEncryptionKey, local.ProtectedEncryptionKey);
        Assert.Equal(operationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        Assert.Equal("Retained", fixture.Client.Profile!.Nickname);
        Assert.Equal("Original", fixture.Client.Profile.Bio);
        Assert.True(fixture.Client.Profile.PublicDiscovery);
        var repeated = fixture.Relay.Requests.Where(value => value.Method == "device.state.publish").ToArray();
        if (boundary == "staged-device")
            Assert.Equal(publication, ProtocolModel.FromJson<AccountDeviceState>(Assert.Single(repeated).Body!)!.ToJson());
        else
            Assert.Empty(repeated);
    }

    [Fact]
    public async Task Completed_profile_is_preserved_when_workflow_cleanup_fails()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_cleanup BEFORE DELETE ON AccountEstablishment BEGIN SELECT RAISE(ABORT, 'cleanup interrupted'); END;", Token);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "Retained", PublicDiscovery = true }
        }, Token));

        var profile = fixture.Client.Profile!.ToJson();
        await using (var db = fixture.Database.Open())
        {
            Assert.Equal(AccountEstablishmentStep.InitializingProfile, (await db.AccountEstablishments.SingleAsync(Token)).Step);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_cleanup;", Token);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(profile, fixture.Client.Profile!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method.EndsWith(".publish", StringComparison.Ordinal) || value.Method == "auth.account.verify");
        Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.resolve");
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }
}
