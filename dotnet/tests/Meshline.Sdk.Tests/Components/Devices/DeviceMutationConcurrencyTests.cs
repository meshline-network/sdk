using Meshline.Interactions;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;
using System.Collections.Immutable;
using System.Text;

namespace Meshline.Tests.Components.Devices;

public sealed class DeviceMutationConcurrencyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standalone_publication_serializes_following_device_mutations(bool cancelWaitingRenewal)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var original = fixture.Client.Device!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var publication = fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task? renewal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            renewal = fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), cancellation.Token);
            Assert.False(renewal.IsCompleted);
            Assert.Same(original, fixture.Client.Device);
            if (cancelWaitingRenewal)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renewal);
            }
        }
        finally
        {
            release.TrySetResult();
            await publication.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (renewal is not null && !cancelWaitingRenewal)
                await renewal.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        fixture.Relay.Handler = null;
        if (cancelWaitingRenewal)
        {
            Assert.Same(original, fixture.Client.Device);
            await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token);
        }
        Assert.NotEqual(original.ToJson(), fixture.Client.Device!.ToJson());
        Assert.Equal(original.GetDeviceId(TestNetwork.Context), fixture.Client.Device.GetDeviceId(TestNetwork.Context));
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        Assert.Equal(fixture.Client.Device.ToJson(), Assert.Single(fixture.Client.DeviceState!.Certificates).ToJson());
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
