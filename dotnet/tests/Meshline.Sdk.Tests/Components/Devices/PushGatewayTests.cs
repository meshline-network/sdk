using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Devices;

public sealed class PushGatewayTests
{
    const string Gateway = "https://PUSH.example.com:443/api/push/notifications?app=meshline&route=chat";
    const string NextGateway = "https://push-next.example.com/callback?";
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_device_declares_supplied_gateway_and_retains_it_after_restart_and_renewal(bool establish)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(establish, Gateway);
        var certificate = establish ? fixture.Client.Device!
            : await fixture.Client.DeviceManager.CreateDeviceAsync(new DeviceCreationOptions { Validity = TimeSpan.FromDays(7), PushGateway = Gateway }, Token);

        Assert.Equal(Gateway, certificate.PushGateway);
        Assert.Null(certificate.Validate(TestNetwork.Context));
        await fixture.ReopenAsync();
        Assert.Equal(certificate.ToJson(), fixture.Client.Device!.ToJson());

        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(30), Token);

        Assert.Equal(Gateway, renewed.PushGateway);
        Assert.Equal(certificate.GetDeviceId(TestNetwork.Context), renewed.GetDeviceId(TestNetwork.Context));
        Assert.Null(renewed.Validate(TestNetwork.Context));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(Gateway, false)]
    [InlineData(Gateway, true)]
    public async Task Renewal_with_omitted_or_unspecified_gateway_retains_the_declaration(string? gateway, bool explicitUnspecified)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(pushGateway: gateway);
        var original = fixture.Client.Device!;
        var options = new DeviceRenewalOptions { Validity = TimeSpan.FromDays(7) };
        if (explicitUnspecified) options.PushGateway = FieldUpdate<string>.Unspecified;

        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(options, Token);

        Assert.Equal(gateway, renewed.PushGateway);
        Assert.Equal(original.GetDeviceId(TestNetwork.Context), renewed.GetDeviceId(TestNetwork.Context));
        Assert.Equal(7 * 86400, renewed.ExpiresAt - renewed.NotBefore);
        Assert.Null(renewed.Validate(TestNetwork.Context));
        await fixture.ReopenAsync();
        Assert.Equal(renewed.ToJson(), fixture.Client.Device!.ToJson());
    }

    [Fact]
    public async Task Gateway_add_change_and_removal_preserve_keys_and_other_devices_and_rebind_sessions()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock).Certificate;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, other], cancellationToken: Token);
        var signature = await fixture.Client.DeviceManager.SignAsync("unchanged device keys"u8.ToArray(), Token);
        LocalDeviceRecord original;
        await using (var db = fixture.Database.Open()) original = await db.LocalDevices.SingleAsync(Token);

        foreach (var gateway in new[] { Gateway, NextGateway, null })
        {
            var oldSession = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
            var previousState = fixture.Client.DeviceState!.ToJson();
            var certificate = await fixture.Client.DeviceManager.RenewDeviceAsync(new DeviceRenewalOptions
            {
                Validity = TimeSpan.FromDays(30),
                PushGateway = gateway is null ? FieldUpdate<string>.Delete : gateway
            }, Token);

            Assert.Equal(gateway, certificate.PushGateway);
            Assert.Equal(deviceId, certificate.GetDeviceId(TestNetwork.Context));
            Assert.Null(certificate.Validate(TestNetwork.Context));
            Assert.Equal(previousState, fixture.Client.DeviceState!.ToJson());
            Assert.DoesNotContain(oldSession, fixture.Pool.Clients);
            await using (var db = fixture.Database.Open())
            {
                var local = await db.LocalDevices.SingleAsync(Token);
                Assert.Equal(certificate.ToJson(), local.CertificateJson);
                Assert.Equal(original.ProtectedSigningKey, local.ProtectedSigningKey);
                Assert.Equal(original.ProtectedEncryptionKey, local.ProtectedEncryptionKey);
            }

            var publication = await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
            Assert.Equal(DeviceStatePublishStatus.Accepted, publication.Status);
            var current = await fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
            Assert.Equal(certificate.ToJson(), current!.Certificates.Single(value => value.GetDeviceId(TestNetwork.Context) == deviceId).ToJson());
            Assert.Contains(current.Certificates, value => value.ToJson() == other.ToJson());
            Assert.Null(current.ValidateDeviceAuthorization(deviceId, TestNetwork.Context));
            await fixture.ReopenAsync();
            Assert.Equal(gateway, fixture.Client.Device!.PushGateway);
            Assert.Equal(signature, await fixture.Client.DeviceManager.SignAsync("unchanged device keys"u8.ToArray(), Token));
        }
        Assert.DoesNotContain("push_gateway", fixture.Client.Device!.ToJson());
    }

    [Fact]
    public async Task Failed_local_update_leaves_certificate_and_sessions_unchanged()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(pushGateway: Gateway);
        var original = fixture.Client.Device!.ToJson();
        var session = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var changes = 0;
        fixture.Client.DeviceManager.DeviceChanged += (_, _) => changes++;
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_update AFTER UPDATE ON LocalDevice BEGIN SELECT RAISE(ABORT, 'interrupted'); END;", Token);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.DeviceManager.RenewDeviceAsync(
            new DeviceRenewalOptions { Validity = TimeSpan.FromDays(7), PushGateway = NextGateway }, Token));

        Assert.Equal(original, fixture.Client.Device!.ToJson());
        Assert.Equal(0, changes);
        Assert.Same(session, await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token));
        await fixture.ReopenAsync();
        Assert.Equal(original, fixture.Client.Device!.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_publication_retains_exact_gateway_change_across_restart(bool remove)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(pushGateway: Gateway);
        var updated = await fixture.Client.DeviceManager.RenewDeviceAsync(new DeviceRenewalOptions
        {
            Validity = TimeSpan.FromDays(30),
            PushGateway = remove ? FieldUpdate<string>.Delete : NextGateway
        }, Token);
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
            ? throw new HttpRequestException("lost publication response") : Task.FromResult(fixture.Relay.Respond(request));

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [updated], cancellationToken: Token));
        var submitted = Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish").Body;
        await fixture.ReopenAsync();
        Assert.Equal(updated.ToJson(), fixture.Client.Device!.ToJson());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.DeviceManager.RenewDeviceAsync(
            new DeviceRenewalOptions { Validity = TimeSpan.FromDays(7), PushGateway = Gateway }, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token));
        fixture.Relay.Handler = null;

        var result = await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [updated], cancellationToken: Token);

        Assert.Equal(DeviceStatePublishStatus.Accepted, result.Status);
        Assert.All(fixture.Relay.Requests.Where(request => request.Method == "device.state.publish"), request => Assert.Equal(submitted, request.Body));
        Assert.Equal(updated.ToJson(), Assert.Single(result.DeviceState.Certificates).ToJson());
        await using var verify = fixture.Database.Open();
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("establish")]
    [InlineData("recover")]
    public async Task Invalid_creation_gateway_is_rejected_before_io(string operation)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Requests.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => operation switch
        {
            "create" => fixture.Client.DeviceManager.CreateDeviceAsync(new DeviceCreationOptions { PushGateway = "http://push.example.com" }, Token),
            "establish" => fixture.Client.EstablishAccountAsync(new() { PushGateway = "http://push.example.com" }, Token),
            _ => fixture.Client.RecoverAccountAsync(new() { PushGateway = "http://push.example.com" }, Token)
        });

        Assert.Empty(fixture.Relay.Requests);
        Assert.Null(fixture.Client.Device);
        await using var db = fixture.Database.Open();
        Assert.Empty(await db.LocalDevices.ToListAsync(Token));
        Assert.Empty(await db.DevicePreparations.ToListAsync(Token));
        Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://push.example.com/callback")]
    [InlineData(null)]
    public async Task Invalid_renewal_gateway_options_leave_certificate_and_sessions_unchanged(string? gateway)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(pushGateway: Gateway);
        var original = fixture.Client.Device!.ToJson();
        var session = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var changes = 0;
        fixture.Client.DeviceManager.DeviceChanged += (_, _) => changes++;
        fixture.Relay.Requests.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.DeviceManager.RenewDeviceAsync(
            new DeviceRenewalOptions { PushGateway = gateway! }, Token));

        Assert.Equal(original, fixture.Client.Device!.ToJson());
        Assert.Equal(0, changes);
        Assert.Empty(fixture.Relay.Requests);
        Assert.Contains(session, fixture.Pool.Clients);
        await using var db = fixture.Database.Open();
        Assert.Equal(original, (await db.LocalDevices.SingleAsync(Token)).CertificateJson);
    }
}
