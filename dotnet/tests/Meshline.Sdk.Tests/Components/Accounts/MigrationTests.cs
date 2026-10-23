using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class MigrationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_recovery_or_migration_supersedes_interrupted_setup(bool migrate)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw new HttpRequestException("profile unavailable") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        byte[] key;
        await using (var db = fixture.Database.Open())
        {
            Assert.True((await db.AccountEstablishments.SingleAsync(Token)).DeviceAuthorizationConfirmed);
            key = (await db.LocalDevices.SingleAsync(Token)).ProtectedSigningKey;
        }
        fixture.Relay.Handler = null;
        using var target = new OfflineRelay("target.test");
        fixture.Relay.LinkTo(target);

        if (migrate) await fixture.Client.ChangeHomeRelayAsync(target.RelayId, Token);
        else await fixture.Client.RecoverAccountAsync(new() { RelayId = fixture.Relay.RelayId }, Token);

        await using (var db = fixture.Database.Open())
        {
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
            Assert.Equal(key, (await db.LocalDevices.SingleAsync(Token)).ProtectedSigningKey);
        }
        await fixture.ReopenAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(migrate ? target.RelayId : fixture.Relay.RelayId, fixture.Client.Route!.RelayId);
    }

    [Theory]
    [InlineData("device.state.publish")]
    [InlineData("account.route.publish")]
    [InlineData("profile.publish")]
    public async Task Home_relay_migration_resumes_at_each_publication_boundary(string interrupted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = new("migrating") }, Token);
        using var target = new OfflineRelay("target.test");
        fixture.Relay.LinkTo(target);
        var fail = true;
        target.Handler = (request, _) =>
        {
            using var accepted = request.Method == interrupted && fail ? target.Respond(request) : null;
            if (accepted is not null)
                throw new HttpRequestException("Response lost after " + interrupted);
            return Task.FromResult(target.Respond(request));
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ChangeHomeRelayAsync(target.RelayId, Token));

        await using (var db = fixture.Database.Open())
            Assert.Equal(target.RelayId, (await db.HomeRelayMigrations.SingleAsync(Token)).TargetRelayId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.ChangeHomeRelayAsync(fixture.Relay.RelayId, Token));

        await fixture.ReopenAsync();
        fail = false;
        await fixture.Client.StartAsync(Token);
        await fixture.Client.StopAsync(Token);

        Assert.Equal(target.RelayId, fixture.Client.Route!.RelayId);
        Assert.Equal("migrating", fixture.Client.Profile!.Nickname);
        Assert.NotNull(target.Devices[fixture.Account.AccountId]);
        Assert.Equal("migrating", target.Profiles[fixture.Account.AccountId].Profile.Nickname);

        await using var verify = fixture.Database.Open();

        Assert.Empty(await verify.HomeRelayMigrations.ToListAsync(Token));
        Assert.All(
            [fixture.Client.AccountManager.LifecycleState, fixture.Client.DeviceManager.LifecycleState, fixture.Client.MessageManager.LifecycleState, fixture.Client.ChannelManager.LifecycleState, fixture.Client.GroupManager.LifecycleState],
            state => Assert.Equal(ComponentState.Stopped, state));
        // Client disposal cannot take ownership of the application-owned pool.
        await fixture.Client.DisposeAsync();
        var relay = await fixture.Pool.GetAsync(target.RelayId, cancellationToken: Token);

        Assert.NotNull(await relay.GetInfoAsync(Token));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_and_migration_renew_the_same_device_and_confirm_its_authorization(bool migrate)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(1) }, Token);
        var original = fixture.Client.Device!;
        var deviceId = original.GetDeviceId(TestNetwork.Context);
        var input = "retained key"u8.ToArray();
        var signature = await fixture.Client.DeviceManager.SignAsync(input, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        using var target = new OfflineRelay("migration.test");
        if (migrate)
        {
            fixture.Relay.LinkTo(target);
            await fixture.Client.ChangeHomeRelayAsync(target.RelayId, Token);
        }
        else
            await fixture.Client.RecoverAccountAsync(new() { RelayId = fixture.Relay.RelayId }, Token);

        Assert.Equal(deviceId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.True(fixture.Client.Device.ExpiresAt > original.ExpiresAt);
        Assert.Equal(signature, await fixture.Client.DeviceManager.SignAsync(input, Token));
        Assert.Equal(migrate ? target.RelayId : fixture.Relay.RelayId, fixture.Client.Route!.RelayId);
        Assert.Null(fixture.Client.DeviceState!.ValidateDeviceAuthorization(deviceId, TestNetwork.Context));
        await using var verify = fixture.Database.Open();
    }
}
