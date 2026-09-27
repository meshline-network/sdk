using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Meshline.Tests.Components.Devices;

public sealed class DeviceTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Device_keys_are_protected_reloaded_and_renewed_without_identity_change()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);

        var device = await fixture.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(10), Token);
        var input = "signed by local device"u8.ToArray();
        var signature = await fixture.Client.DeviceManager.SignAsync(input, Token);

        Assert.True(Ed25519.Verify(signature, device.SigningPublicKey.AsSpan(), input));

        await using (var db = fixture.Database.Open())
        {
            var stored = await db.LocalDevices.SingleAsync(Token);

            Assert.True(stored.ProtectedSigningKey.Length > 32);
            Assert.True(stored.ProtectedEncryptionKey.Length > 32);
        }

        await fixture.ReopenAsync();

        Assert.Equal(signature, await fixture.Client.DeviceManager.SignAsync(input, Token));

        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(20), Token);

        Assert.Equal(device.GetDeviceId(TestNetwork.Context), renewed.GetDeviceId(TestNetwork.Context));
        Assert.True(renewed.ExpiresAt > device.ExpiresAt);
    }

    [Fact]
    public async Task Existing_device_rejects_duplicate_creation()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(1), Token));
    }

    [Fact]
    public async Task Device_key_agreement_rejects_invalid_peer_key()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.DeviceManager.DeriveSharedSecretAsync(new byte[32], Token));
    }

    [Fact]
    public async Task Secret_protection_failure_does_not_persist_half_created_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);

        fixture.Protector.Fail = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(1), Token));
        Assert.Null(fixture.Client.Device);

        await using (var db = fixture.Database.Open())
        {
            Assert.Empty(await db.LocalDevices.ToListAsync(Token));
            Assert.Null((await db.Bindings.SingleAsync(Token)).DeviceId);
        }

        fixture.Protector.Fail = false;

        Assert.NotNull(await fixture.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(1), Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(721)]
    public async Task Device_validity_is_bounded(int days)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(days), Token));
    }

    [Fact]
    public async Task Revoking_local_device_removes_authorization_and_duplicate_removal_is_noop()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var id = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        await fixture.Client.DeviceManager.RemoveDeviceAsync(id, Token);

        Assert.Null(fixture.Client.DeviceManager.GetCertificate(id));
        Assert.NotNull(fixture.Client.DeviceState!.ValidateDeviceAuthorization(id, TestNetwork.Context));

        var publications = fixture.Relay.Requests.Count(request => request.Method == "device.state.publish");
        await fixture.Client.DeviceManager.RemoveDeviceAsync(id, Token);

        Assert.Equal(publications, fixture.Relay.Requests.Count(request => request.Method == "device.state.publish"));

        await fixture.ReopenAsync();

        Assert.Null(fixture.Client.DeviceManager.GetCertificate(id));
    }
}
