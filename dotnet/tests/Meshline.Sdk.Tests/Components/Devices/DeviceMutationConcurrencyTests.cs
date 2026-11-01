using Meshline.Components;
using Meshline.Interactions;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text;

namespace Meshline.Tests.Components.Devices;

public sealed class DeviceMutationConcurrencyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_profile_does_not_wait_for_superseded_cold_key_loading()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "initial" }, Token);
        var protector = new PausedKeyProtector(fixture.Protector);
        await using var devices = new DeviceManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Client.AccountManager, fixture.Account, protector);
        await devices.InitializeAsync(Token);
        await using var profiles = new ProfileManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Client.AccountManager, devices);
        await profiles.InitializeAsync(Token);
        var old = profiles.UpdateProfileAsync(new() { Nickname = "old", Bio = "retained" }, Token);
        try
        {
            await protector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = profiles.UpdateProfileAsync(new() { Nickname = "latest" }, Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => old);
            var result = await latest.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal("latest", result.Nickname);
            Assert.Equal("retained", result.Bio);
            var disposal = profiles.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            protector.Release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.NotNull(protector.DelayedKey);
            Assert.All(protector.DelayedKey, value => Assert.Equal(0, value));
            await using var database = fixture.Database.Open();
            Assert.Equal(result.ToJson(), (await database.AccountProfiles.SingleAsync(Token)).DocumentJson);
            Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);
        }
        finally
        {
            protector.Release.TrySetResult();
            await Record.ExceptionAsync(() => old);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_key_loading_does_not_block_other_calls_and_disposal_clears_late_plaintext(bool encryption)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var input = encryption ? fixture.Client.DeviceManager.Local!.EncryptionPublicKey.ToArray() : "signature"u8.ToArray();
        Task<byte[]> UseKeyAsync(DeviceManager manager, CancellationToken token) => encryption
            ? manager.DeriveSharedSecretAsync(input, token) : manager.SignAsync(input, token);
        var expected = await UseKeyAsync(fixture.Client.DeviceManager, Token);
        var protector = new PausedKeyProtector(fixture.Protector);
        await using var devices = new DeviceManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Client.AccountManager, fixture.Account, protector);
        await devices.InitializeAsync(Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var old = UseKeyAsync(devices, cancellation.Token);
        try
        {
            await protector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await cancellation.CancelAsync();
            Assert.Equal(expected, await UseKeyAsync(devices, Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
            var disposal = devices.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            protector.Release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
            Assert.NotNull(protector.DelayedKey);
            Assert.All(protector.DelayedKey, value => Assert.Equal(0, value));
        }
        finally
        {
            protector.Release.TrySetResult();
            await Record.ExceptionAsync(() => old);
        }
    }

    sealed class PausedKeyProtector(ISecretProtector inner) : ISecretProtector
    {
        int calls;
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();
        public byte[]? DelayedKey { get; private set; }
        public Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken = default) =>
            inner.ProtectAsync(plaintext, purpose, cancellationToken);
        public async Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> protectedData, string purpose, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) != 1) return await inner.UnprotectAsync(protectedData, purpose, cancellationToken);
            Entered.TrySetResult();
            await Release.Task; // A platform key store can ignore cancellation.
            return DelayedKey = await inner.UnprotectAsync(protectedData, purpose, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_state_does_not_wait_for_retirement_of_a_revoked_device_session(bool disposeDuringRetirement)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.DisposeAsync();
        await using var pool = fixture.Relay.Pool(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            pool, fixture.Protector, fixture.Account);
        await client.InitializeAsync(Token);
        var certificate = client.Device!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var authentications = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            if (request.Method == "auth.device.verify" && Interlocked.Increment(ref authentications) == 1)
            {
                entered.TrySetResult();
                await release.Task; // Retirement must drain even transports that ignore cancellation.
            }
            return response;
        };
        var authenticating = pool.GetAsync(fixture.Relay.RelayId, client.DeviceManager, Token);
        Task? revoking = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            revoking = client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [], cancellationToken: Token);
            await AsyncTest.UntilAsync(() => client.DeviceState!.Certificates.IsEmpty);
            var latest = await client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [certificate], cancellationToken: Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => revoking);
            Assert.Equal(certificate.ToJson(), Assert.Single(latest.DeviceState.Certificates).ToJson());
            var replacement = await pool.GetAsync(fixture.Relay.RelayId, client.DeviceManager, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            var retired = AsyncTest.Signal();
            pool.PoolChanged += (_, _) => retired.TrySetResult();
            var disposing = disposeDuringRetirement ? client.DeviceManager.DisposeAsync().AsTask() : null;
            if (disposing is not null) Assert.False(disposing.IsCompleted);
            release.TrySetResult();
            await Record.ExceptionAsync(() => authenticating.WaitAsync(TimeSpan.FromSeconds(5), Token));
            if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(5), Token);
            else
            {
                await retired.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
                Assert.Same(replacement, await pool.GetAsync(fixture.Relay.RelayId, client.DeviceManager, Token));
            }
            Assert.Equal(latest.DeviceState.ToJson(), client.DeviceState!.ToJson());
            await using var database = fixture.Database.Open();
            Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
            Assert.Equal(latest.DeviceState.ToJson(), (await database.DeviceStates.SingleAsync(value => value.AccountId == fixture.Account.AccountId, Token)).DocumentJson);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => authenticating);
            if (revoking is not null) await Record.ExceptionAsync(() => revoking);
        }
    }

    [Fact]
    public async Task Renewal_and_a_new_publication_do_not_wait_for_an_unfinished_state_request()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var original = fixture.Client.Device!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var count = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish" && Interlocked.Increment(ref count) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var publication = fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<Meshline.Components.StateUpdateSupersededException>(() => publication);
            Assert.Equal(fixture.Client.Device!.ToJson(), Assert.Single(latest.DeviceState.Certificates).ToJson());
            Assert.NotEqual(original.ToJson(), fixture.Client.Device.ToJson());
            Assert.Equal(original.GetDeviceId(TestNetwork.Context), fixture.Client.Device.GetDeviceId(TestNetwork.Context));
        }
        finally { release.TrySetResult(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Account_signing_does_not_hold_the_key_or_database_writer_lock(bool establishment)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
        await fixture.Client.DisposeAsync();
        var signer = new PausingCertificateSigner(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        var originalId = client.Device!.GetDeviceId(TestNetwork.Context);
        // Populate metadata and session caches before the concurrent enqueue.
        await client.MessageManager.SendMessageAsync(fixture.Account.AccountId,
            new() { Body = new() { ContentType = "text/plain", Text = "warmup" } }, Token);
        await client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
        if (establishment) fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task mutation = establishment ? client.EstablishAccountAsync(cancellationToken: cancellation.Token)
            : client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(365), cancellation.Token);
        Task? enqueue = null;
        try
        {
            await signer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            enqueue = client.MessageManager.SendMessageAsync(fixture.Account.AccountId,
                new() { Body = new() { ContentType = "text/plain", Text = "concurrent" } }, cancellation.Token);
            if (establishment)
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => enqueue.WaitAsync(TimeSpan.FromSeconds(5), Token));
                Assert.Equal(64, (await client.DeviceManager.SignAsync(new byte[] { 1 }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token)).Length);
            }
            else
                await enqueue.WaitAsync(TimeSpan.FromSeconds(5), Token);

            // The wallet is still paused; both crypto work and an unrelated writer
            // must complete without waiting for the device activation transaction.
            await using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fixture.Database.Options.Path,
                DefaultTimeout = 1,
                Pooling = false
            }.ToString());
            await probe.OpenAsync(Token);
            await using var transaction = probe.BeginTransaction(deferred: false);
            await transaction.RollbackAsync(Token);
        }
        finally
        {
            signer.Release.TrySetResult();
            // Drain failed assertions too, so no blocked task survives the fixture.
            if (enqueue is { IsCompleted: false }) cancellation.Cancel();
            if (enqueue is not null) await Record.ExceptionAsync(() => enqueue);
            await mutation.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }

        var currentId = client.Device!.GetDeviceId(TestNetwork.Context);
        Assert.Equal(originalId, currentId);
        if (establishment)
            Assert.Contains(fixture.Relay.Devices[fixture.Account.AccountId].Certificates,
                value => value.GetDeviceId(TestNetwork.Context) == currentId);
    }

    sealed class PausingCertificateSigner(AccountSigner inner) : IAccountSigner
    {
        public string AccountId => inner.AccountId;
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();

        public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetString(data.Span).Contains("meshline.device.certificate", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.SignAsync(data, cancellationToken);
        }
    }
}
