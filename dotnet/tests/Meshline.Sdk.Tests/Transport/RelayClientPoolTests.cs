using Meshline.Interactions;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;

namespace Meshline.Tests.Transport;

public sealed class RelayClientPoolTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pool_reuses_public_clients_coalesces_authentication_and_separates_modes()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var unauthenticated = await pool.GetAsync(relay.RelayId, cancellationToken: Token);

        Assert.Empty(relay.Requests);

        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        relay.Handler = async (request, token) =>
        {
            if (request.Method == "auth.challenge")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            return relay.Respond(request);
        };
        var pending = Enumerable.Range(0, 16).Select(_ => pool.GetAsync(relay.RelayId, account, Token)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.All(pending, task => Assert.False(task.IsCompleted));

        release.TrySetResult();
        var accounts = await Task.WhenAll(pending);

        Assert.All(accounts, item => Assert.Same(unauthenticated, item));
        Assert.Equal(1, relay.AuthenticationCount);

        var device = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), cancellationToken: Token);

        Assert.NotSame(unauthenticated, device);
        Assert.Equal(SessionMode.Device, device.SessionMode);
        Assert.Equal(SessionMode.Account, unauthenticated.SessionMode);
        Assert.Equal(2, pool.Clients.Count);

        using var other = new AccountSigner();

        await Assert.ThrowsAsync<ArgumentException>(() => pool.GetAsync(relay.RelayId, other, cancellationToken: Token));

        await pool.DisposeAsync();

        Assert.Empty(pool.Clients);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.GetAsync(relay.RelayId, cancellationToken: Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_pool_accepts_same_device_renewal_and_rejects_retired_certificates(bool replaceSigner)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var pool = fixture.Relay.Pool(fixture.Account);
        var signer = new CertificateSigner(fixture.Client.DeviceManager, fixture.Client.Device!);
        var previous = ProtocolModel.FromJson<DeviceCertificate>(signer.Certificate.ToJson())!;
        var original = await pool.GetAsync(fixture.Relay.RelayId, signer, Token);
        var account = await pool.GetAsync(fixture.Relay.RelayId, fixture.Account, Token);
        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token);
        // Certificates have no revision: renewal can shorten validity within the same second.
        Assert.Equal(previous.NotBefore, renewed.NotBefore);
        Assert.True(renewed.ExpiresAt < previous.ExpiresAt);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        if (replaceSigner) signer = new CertificateSigner(fixture.Client.DeviceManager, renewed);
        else signer.Certificate = renewed;
        fixture.Relay.Requests.Clear();

        var clients = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => pool.GetAsync(fixture.Relay.RelayId, signer, Token)));

        var current = clients[0];
        Assert.All(clients, client => Assert.Same(current, client));
        Assert.NotSame(original, current);
        Assert.DoesNotContain(original, pool.Clients);
        Assert.Same(account, await pool.GetAsync(fixture.Relay.RelayId, fixture.Account, Token));
        Assert.Single(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
        var stale = new CertificateSigner(fixture.Client.DeviceManager, previous);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.GetAsync(fixture.Relay.RelayId, stale, Token));
        Assert.Equal(0, stale.SignCalls);
        var restored = new CertificateSigner(fixture.Client.DeviceManager, ProtocolModel.FromJson<DeviceCertificate>(renewed.ToJson())!);
        Assert.Same(current, await pool.GetAsync(fixture.Relay.RelayId, restored, Token));
        Assert.Equal(0, restored.SignCalls);
        Assert.Single(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acquisition_started_before_renewal_cannot_restore_its_certificate(bool previouslyBound)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var pool = fixture.Relay.Pool(fixture.Account);
        var previous = fixture.Client.Device!;
        if (previouslyBound)
            await pool.GetAsync(fixture.Relay.RelayId, new CertificateSigner(fixture.Client.DeviceManager, previous), Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var stale = new CertificateSigner(fixture.Client.DeviceManager, previous, () =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10), Token).GetAwaiter().GetResult();
        });
        var acquiring = Task.Run(() => pool.GetAsync(fixture.Relay.RelayId, stale, Token), Token);
        RelayClient current;
        CertificateSigner signer;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token);
            await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
            signer = new CertificateSigner(fixture.Client.DeviceManager, renewed);
            current = await pool.GetAsync(fixture.Relay.RelayId, signer, Token);
        }
        finally { release.TrySetResult(); }

        await Assert.ThrowsAsync<InvalidOperationException>(() => acquiring);
        Assert.Equal(0, stale.SignCalls);
        Assert.Same(current, await pool.GetAsync(fixture.Relay.RelayId, signer, Token));
    }

    [Fact]
    public async Task Repeated_device_invalidation_waits_for_sessions_already_removed_from_the_pool()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var accountClient = await pool.GetAsync(relay.RelayId, account, Token);
        var device = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        EventHandler retiring = (_, _) =>
        {
            if (pool.Clients.Contains(device)) return;
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10), Token).GetAwaiter().GetResult();
        };
        pool.PoolChanged += retiring;
        var first = pool.InvalidateDeviceAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.DoesNotContain(device, pool.Clients);
            var second = pool.InvalidateDeviceAsync();
            Assert.False(second.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Same(accountClient, await pool.GetAsync(relay.RelayId, account, Token));
        }
        finally
        {
            release.TrySetResult();
            pool.PoolChanged -= retiring;
            await first.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
    }

    [Fact]
    public async Task Completed_retirement_failure_is_retained_for_disposal_without_blocking_new_sessions()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var pool = relay.Pool(account);
        var signer = new DeviceSigner(account, relay.Clock);
        var original = await pool.GetAsync(relay.RelayId, signer, Token);
        EventHandler reject = (_, _) => throw new InvalidOperationException("retirement observer failed");
        try
        {
            pool.PoolChanged += reject;
            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.InvalidateDeviceAsync());
            pool.PoolChanged -= reject;

            await pool.InvalidateDeviceAsync();
            var replacement = await pool.GetAsync(relay.RelayId, signer, Token);
            Assert.NotSame(original, replacement);
            Assert.Equal(SessionMode.Device, replacement.SessionMode);
        }
        finally
        {
            pool.PoolChanged -= reject;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.DisposeAsync());
            Assert.Equal("retirement observer failed", error.Message);
        }
    }

    sealed class CertificateSigner(IDeviceSigner signer, DeviceCertificate certificate, Action? readCertificate = null) : IDeviceSigner
    {
        public DeviceCertificate Certificate
        {
            get
            {
                var snapshot = certificate;
                readCertificate?.Invoke();
                return snapshot;
            }
            set => certificate = value;
        }
        public int SignCalls { get; private set; }

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            SignCalls++;
            return signer.SignAsync(data, cancellationToken);
        }
    }
}
