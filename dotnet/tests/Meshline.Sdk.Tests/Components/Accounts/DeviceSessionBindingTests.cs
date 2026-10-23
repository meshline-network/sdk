using Meshline.Interactions;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class DeviceSessionBindingTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Activation_replaces_old_sessions_once_and_repeat_setup_preserves_the_new_session()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
        var original = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var previousId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        var replacement = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);

        Assert.NotSame(original, replacement);
        Assert.Single(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Same(replacement, await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
    }

    [Fact]
    public async Task Old_identity_or_certificate_cannot_reenter_the_pool_during_activation_retirement()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
        var previous = new RetiredSigner(fixture.Client.Device!);
        var previousId = previous.Certificate.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        var attempted = false;
        Exception? rejection = null;
        fixture.Pool.PoolChanged += (_, _) =>
        {
            if (attempted || fixture.Client.Device!.ToJson() == previous.Certificate.ToJson()) return;
            attempted = true;
            rejection = Record.Exception(() => fixture.Pool.GetAsync(fixture.Relay.RelayId, previous, Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token).GetAwaiter().GetResult());
        };

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.True(attempted);
        Assert.IsType<InvalidOperationException>(rejection);
        Assert.Equal(0, previous.SignCalls);
        Assert.Equal(previousId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
    }

    [Fact]
    public async Task Deserialized_identical_certificate_reuses_the_session_without_signing()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var original = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(fixture.Client.Device!.ToJson())!;
        Assert.NotSame(fixture.Client.Device, certificate);
        var signer = new RetiredSigner(certificate);
        fixture.Relay.Requests.Clear();

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Same(original, await fixture.Pool.GetAsync(fixture.Relay.RelayId, signer, Token));
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Same(original, await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token));
        Assert.Equal(0, signer.SignCalls);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "auth.device.verify" or "device.state.publish");
    }

    [Fact]
    public async Task Public_renewal_retires_only_device_sessions_and_authenticates_once()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var original = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var account = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Account, Token);
        var previousId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Requests.Clear();

        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token);

        Assert.Equal(previousId, renewed.GetDeviceId(TestNetwork.Context));
        Assert.DoesNotContain(original, fixture.Pool.Clients);
        Assert.Same(account, await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Account, Token));
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [renewed], cancellationToken: Token);
        var replacement = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Same(replacement, await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token));
        Assert.Single(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
    }

    [Fact]
    public async Task Request_that_read_the_old_certificate_before_renewal_cannot_restore_it()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var signer = new RetiredSigner(fixture.Client.Device!, () =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10), Token).GetAwaiter().GetResult();
        });
        var acquiring = Task.Run(() => fixture.Pool.GetAsync(fixture.Relay.RelayId, signer, Token), Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token);
        }
        finally { release.TrySetResult(); }

        await Assert.ThrowsAsync<InvalidOperationException>(() => acquiring);

        Assert.Equal(0, signer.SignCalls);
        var current = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        Assert.Same(current, await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token));
    }

    [Fact]
    public async Task Restart_after_certificate_commit_rebinds_the_retained_pool_without_recreating_keys()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var original = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var certificate = fixture.Client.Device! with { ExpiresAt = fixture.Client.Device.ExpiresAt + 60, DeviceSignature = [], AccountSignature = [] };
        certificate = certificate with
        {
            DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(certificate.GetDeviceSigningInput(TestNetwork.Context), Token)]
        };
        certificate = certificate with { AccountSignature = [.. fixture.Account.Sign(certificate.GetAccountSigningInput(TestNetwork.Context))] };
        byte[] key;
        var operationId = Guid.NewGuid();
        await using (var database = fixture.Database.Open())
        {
            // Model the committed activation boundary before the original pool was notified.
            var local = await database.LocalDevices.SingleAsync(Token);
            key = local.ProtectedSigningKey;
            local.CertificateJson = certificate.ToJson();
            (await database.DevicePreparations.SingleAsync(Token)).ActivatedOperationId = operationId;
            database.AccountEstablishments.Add(new()
            {
                OperationId = operationId,
                RelayId = fixture.Relay.RelayId,
                CertificateValiditySeconds = certificate.ExpiresAt - certificate.NotBefore,
                RouteValiditySeconds = 86400,
                PreviousLocalDeviceId = local.DeviceId
            });
            await database.SaveChangesAsync(Token);
        }
        await fixture.Client.DisposeAsync();
        await using var restarted = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Protector, fixture.Account);
        await restarted.InitializeAsync(Token);
        fixture.Relay.Requests.Clear();

        await restarted.EstablishAccountAsync(cancellationToken: Token);

        Assert.DoesNotContain(original, fixture.Pool.Clients);
        Assert.Equal(certificate.ToJson(), restarted.Device!.ToJson());
        // The still-valid old state permits a device-authenticated read; publication
        // then invalidates that authorization before the final confirmation.
        Assert.Equal(2, fixture.Relay.Requests.Count(request => request.Method == "auth.device.verify"));
        Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        var current = await fixture.Pool.GetAsync(fixture.Relay.RelayId, restarted.DeviceManager, Token);
        fixture.Relay.Requests.Clear();
        await restarted.EstablishAccountAsync(cancellationToken: Token);
        Assert.Same(current, await fixture.Pool.GetAsync(fixture.Relay.RelayId, restarted.DeviceManager, Token));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "auth.device.verify" or "device.state.publish");
        await using var verify = fixture.Database.Open();
        Assert.Equal(key, (await verify.LocalDevices.SingleAsync(Token)).ProtectedSigningKey);
        Assert.False(await verify.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Equal(operationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
    }

    [Fact]
    public async Task Renewal_retires_an_inflight_authentication_for_the_previous_certificate()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.DisposeAsync();
        await using var pool = fixture.Relay.Pool(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            pool, fixture.Protector, fixture.Account);
        await client.InitializeAsync(Token);
        var entered = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "auth.device.verify")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return fixture.Relay.Respond(request);
        };
        var authenticating = pool.GetAsync(fixture.Relay.RelayId, client.DeviceManager, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        await client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token).WaitAsync(TimeSpan.FromSeconds(5), Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authenticating);
        Assert.Empty(pool.Clients);
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        var current = await pool.GetAsync(fixture.Relay.RelayId, client.DeviceManager, Token);
        Assert.Same(current, await pool.GetAsync(fixture.Relay.RelayId, client.DeviceManager, Token));
        Assert.Single(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
    }

    [Fact]
    public async Task Rebinding_an_unrelated_pool_preserves_its_existing_device_session()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var original = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using var other = new MeshlineClient(fixture.Relay.Options(fixture.Account), database.Options,
            fixture.Pool, new SecretProtector(), fixture.Account);
        await other.InitializeAsync(Token);
        fixture.Relay.Requests.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => other.EstablishAccountAsync(cancellationToken: Token));

        Assert.Same(original, await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "auth.device.verify" or "device.state.publish");
    }

    sealed class RetiredSigner(DeviceCertificate certificate, Action? readCertificate = null) : IDeviceSigner
    {
        public DeviceCertificate Certificate
        {
            get
            {
                readCertificate?.Invoke();
                return certificate;
            }
        }
        public int SignCalls { get; private set; }

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            SignCalls++;
            throw new InvalidOperationException("The old identity reached authentication after activation.");
        }
    }
}
