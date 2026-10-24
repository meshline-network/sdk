using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class PushGatewayEstablishmentTests
{
    const string Gateway = "https://PUSH.example.com:443/api/push/notifications?app=meshline&route=chat";
    const string NextGateway = "https://push-next.example.com/callback?";
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData(Gateway)]
    public async Task Healthy_existing_device_ignores_creation_gateway(string? gateway)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(pushGateway: gateway);
        var original = fixture.Client.Device!.ToJson();
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(new() { PushGateway = NextGateway }, Token);

        Assert.Equal(original, fixture.Client.Device!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_certificate_renewal_preserves_current_declaration_including_removal(bool remove)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(pushGateway: Gateway);
        var expected = remove ? null : NextGateway;
        await fixture.Client.DeviceManager.RenewDeviceAsync(new DeviceRenewalOptions
        {
            Validity = TimeSpan.FromSeconds(10),
            PushGateway = remove ? FieldUpdate<string>.Delete : NextGateway
        }, Token);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.ReopenAsync();

        await fixture.Client.EstablishAccountAsync(new() { PushGateway = Gateway }, Token);

        Assert.Equal(expected, fixture.Client.Device!.PushGateway);
        Assert.Equal(deviceId, fixture.Client.Device.GetDeviceId(TestNetwork.Context));
        Assert.Equal(expected, Assert.Single(fixture.Client.DeviceState!.Certificates).PushGateway);
        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(1), Token);
        Assert.Equal(expected, renewed.PushGateway);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("valid")]
    [InlineData("expired")]
    [InlineData("removed")]
    public async Task Recovery_uses_creation_gateway_only_when_it_creates_a_device(string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        var expected = scenario == "new" ? Gateway : scenario == "removed" ? null : NextGateway;
        await fixture.InitializeAsync(scenario != "new", expected);
        if (scenario is "expired" or "removed")
        {
            await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromSeconds(10), Token);
            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        }
        var previousId = fixture.Client.Device?.GetDeviceId(TestNetwork.Context);

        await fixture.Client.RecoverAccountAsync(new() { RelayId = fixture.Relay.RelayId, PushGateway = Gateway }, Token);

        Assert.Equal(expected, fixture.Client.Device!.PushGateway);
        if (previousId is not null) Assert.Equal(previousId, fixture.Client.Device.GetDeviceId(TestNetwork.Context));
        Assert.Equal(expected, Assert.Single(fixture.Client.DeviceState!.Certificates).PushGateway);
    }

    [Theory]
    [InlineData(NextGateway)]
    [InlineData("http://push.example.com/invalid")]
    public async Task Establishment_uses_gateway_supplied_before_asynchronous_work(string changedGateway)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var options = new AccountEstablishmentOptions { PushGateway = Gateway };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return fixture.Relay.Respond(request);
        };

        var establishment = fixture.Client.EstablishAccountAsync(options, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            options.PushGateway = changedGateway;
        }
        finally
        {
            release.TrySetResult();
        }
        await establishment;

        Assert.Equal(Gateway, fixture.Client.Device!.PushGateway);
        Assert.Equal(Gateway, Assert.Single(fixture.Client.DeviceState!.Certificates).PushGateway);
        Assert.Null(fixture.Client.Device.Validate(TestNetwork.Context));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Gateway)]
    public async Task Establishment_retains_creation_intent_before_a_draft_exists(string? gateway)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Protector.Fail = true;

        var failure = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { PushGateway = gateway }, Token));
        Assert.Equal(AccountEstablishmentReason.LocalKeyAccessRequired, failure.Reason);
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            operationId = setup.OperationId;
            Assert.Equal(gateway, setup.InitialPushGateway);
            Assert.Empty(await db.DevicePreparations.ToListAsync(Token));
            Assert.Empty(await db.LocalDevices.ToListAsync(Token));
        }
        fixture.Protector.Fail = false;
        await fixture.ReopenAsync();

        var conflict = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { PushGateway = NextGateway }, Token));
        Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, conflict.Reason);
        Assert.Null(fixture.Client.Device);
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(operationId, setup.OperationId);
            Assert.Equal(gateway, setup.InitialPushGateway);
        }

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(gateway, fixture.Client.Device!.PushGateway);
        Assert.Equal(gateway, Assert.Single(fixture.Client.DeviceState!.Certificates).PushGateway);
    }

    [Fact]
    public async Task Interrupted_establishment_retains_original_gateway_after_caller_mutation_and_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Protector.Fail = true;
        var options = new AccountEstablishmentOptions { PushGateway = Gateway };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return fixture.Relay.Respond(request);
        };

        var establishment = fixture.Client.EstablishAccountAsync(options, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            options.PushGateway = "http://push.example.com/invalid";
        }
        finally
        {
            release.TrySetResult();
        }
        var failure = await Assert.ThrowsAsync<AccountEstablishmentException>(() => establishment);
        Assert.Equal(AccountEstablishmentReason.LocalKeyAccessRequired, failure.Reason);
        await using (var db = fixture.Database.Open())
        {
            Assert.Equal(Gateway, (await db.AccountEstablishments.SingleAsync(Token)).InitialPushGateway);
            Assert.Empty(await db.DevicePreparations.ToListAsync(Token));
        }
        fixture.Relay.Handler = null;
        fixture.Protector.Fail = false;
        await fixture.ReopenAsync();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(Gateway, fixture.Client.Device!.PushGateway);
        Assert.Equal(Gateway, Assert.Single(fixture.Client.DeviceState!.Certificates).PushGateway);
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Gateway)]
    public async Task Initial_draft_keeps_gateway_and_identity_when_it_expires_before_resume(string? gateway)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_activation AFTER UPDATE OF DeviceId ON DatabaseBinding BEGIN SELECT RAISE(ABORT, 'interrupted'); END;", Token);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(new() { PushGateway = gateway, CertificateValidity = TimeSpan.FromSeconds(10) }, Token));
        DevicePreparationRecord saved;
        DeviceCertificate draft;
        await using (var db = fixture.Database.Open())
        {
            saved = await db.DevicePreparations.SingleAsync(Token);
            draft = ProtocolModel.FromJson<DeviceCertificate>(saved.DraftCertificateJson!)!;
            Assert.Equal(gateway, draft.PushGateway);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_activation;", Token);
        }
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.ReopenAsync();
        var conflict = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { PushGateway = NextGateway }, Token));
        Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, conflict.Reason);

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(gateway, fixture.Client.Device!.PushGateway);
        Assert.Equal(draft.GetDeviceId(TestNetwork.Context), fixture.Client.Device.GetDeviceId(TestNetwork.Context));
        Assert.True(fixture.Client.Device.ExpiresAt > draft.ExpiresAt);
        await using var verify = fixture.Database.Open();
        var local = await verify.LocalDevices.SingleAsync(Token);
        foreach (var (kind, before, after) in new[]
        {
            ("signing", saved.ProtectedSigningKey!, local.ProtectedSigningKey),
            ("encryption", saved.ProtectedEncryptionKey!, local.ProtectedEncryptionKey)
        })
        {
            var purpose = $"Meshline/device-{kind}/v1/{TestNetwork.Context}/{fixture.Account.AccountId}/{local.DeviceId}";
            Assert.Equal(await fixture.Protector.UnprotectAsync(before, purpose, Token),
                await fixture.Protector.UnprotectAsync(after, purpose, Token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_expired_draft_retains_its_declaration_and_keys_on_resume(bool remove)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(pushGateway: Gateway);
        var expected = remove ? null : NextGateway;
        await fixture.Client.DeviceManager.RenewDeviceAsync(new DeviceRenewalOptions
        {
            Validity = TimeSpan.FromSeconds(10),
            PushGateway = remove ? FieldUpdate<string>.Delete : NextGateway
        }, Token);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_activation AFTER UPDATE ON LocalDevice BEGIN SELECT RAISE(ABORT, 'interrupted'); END;", Token);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10), PushGateway = Gateway }, Token));
        DeviceCertificate draft;
        await using (var db = fixture.Database.Open())
        {
            draft = ProtocolModel.FromJson<DeviceCertificate>((await db.DevicePreparations.SingleAsync(Token)).DraftCertificateJson!)!;
            Assert.Equal(expected, draft.PushGateway);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_activation;", Token);
        }
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.ReopenAsync();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(expected, fixture.Client.Device!.PushGateway);
        Assert.Equal(draft.GetDeviceId(TestNetwork.Context), fixture.Client.Device.GetDeviceId(TestNetwork.Context));
        Assert.True(fixture.Client.Device.ExpiresAt > draft.ExpiresAt);
        Assert.Null(fixture.Client.DeviceState!.ValidateDeviceAuthorization(draft.GetDeviceId(TestNetwork.Context), TestNetwork.Context));
    }
}
