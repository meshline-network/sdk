using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;

namespace Meshline.Tests.Components.Devices;

public sealed class DeviceAuthorizationIntegrationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Establishment_resumes_public_publication_with_the_same_keys_and_signed_request(bool restart, bool lostResponse)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var manager = fixture.Client.DeviceManager;
        var changed = 0;
        manager.DeviceChanged += (sender, _) => { Assert.Same(manager, sender); changed++; };
        var certificate = await manager.CreateDeviceAsync(TimeSpan.FromDays(7), Token);
        var input = "shared device identity"u8.ToArray();
        var signature = await manager.SignAsync(input, Token);
        if (lostResponse)
            fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
                ? throw new HttpRequestException("response unknown") : Task.FromResult(fixture.Relay.Respond(request));
        if (lostResponse)
            await Assert.ThrowsAsync<HttpRequestException>(() => manager.PublishDeviceStateAsync(fixture.Relay.RelayId, [certificate], cancellationToken: Token));
        else
            Assert.Equal(DeviceStatePublishStatus.Staged, (await manager.PublishDeviceStateAsync(fixture.Relay.RelayId, [certificate], cancellationToken: Token)).Status);
        var pending = Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish").Body;
        // A different entry point cannot change the certificate under an unresolved request.
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RenewDeviceAsync(TimeSpan.FromDays(30), Token));
        if (restart) await fixture.ReopenAsync();
        fixture.Relay.Handler = null;

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(certificate.ToJson(), fixture.Client.Device!.ToJson());
        Assert.Equal(signature, await fixture.Client.DeviceManager.SignAsync(input, Token));
        Assert.Equal(1, changed);
        var submissions = fixture.Relay.Requests.Where(value => value.Method == "device.state.publish").ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.All(submissions, value => Assert.Equal(pending, value.Body));
        await using var db = fixture.Database.Open();
        Assert.False((await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
        Assert.False(await db.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));
        Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
        Assert.Single(fixture.Client.DeviceState!.Certificates);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Establishment_publishes_a_locally_renewed_certificate_and_resumes_its_interrupted_submission(bool restart, bool interrupt)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromMinutes(1) }, Token);
        var original = fixture.Client.Device!;
        var input = "retained renewal keys"u8.ToArray();
        var signature = await fixture.Client.DeviceManager.SignAsync(input, Token);
        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token);
        if (restart) await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        string? pendingRequest = null;
        if (interrupt)
        {
            fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
                ? throw new HttpRequestException("renewal outcome unknown") : Task.FromResult(fixture.Relay.Respond(request));
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
            pendingRequest = Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish").Body;
            await using (var db = fixture.Database.Open())
            {
                Assert.True((await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
                Assert.Single(await db.AccountEstablishments.ToListAsync(Token));
            }
            await fixture.ReopenAsync();
            fixture.Relay.Handler = null;
            fixture.Relay.Requests.Clear();
        }

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(original.GetDeviceId(TestNetwork.Context), renewed.GetDeviceId(TestNetwork.Context));
        Assert.Equal(renewed.ToJson(), fixture.Client.Device!.ToJson());
        Assert.Equal(renewed.ToJson(), Assert.Single(fixture.Client.DeviceState!.Certificates).ToJson());
        Assert.Equal(signature, await fixture.Client.DeviceManager.SignAsync(input, Token));
        var publication = Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        if (interrupt) Assert.Equal(pendingRequest, publication.Body);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "account.route.publish" or "profile.publish");
        await using var verify = fixture.Database.Open();
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));

        // Passing the old expiry must not require a second renewal or registration.
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        Assert.Equal(renewed.ToJson(), fixture.Client.Device!.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_certificate_mutation_cannot_displace_an_interrupted_authorization_draft(bool renewal)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        if (renewal)
        {
            await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        }
        await fixture.Client.DisposeAsync();
        var signer = new SwitchableSigner(fixture.Account) { DeclineCertificate = true };
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.SignatureDeclined, error.Reason);
        string draft;
        await using (var db = fixture.Database.Open())
            draft = Assert.IsType<string>((await db.DevicePreparations.SingleAsync(Token)).DraftCertificateJson);
        signer.DeclineCertificate = false;
        var signatures = signer.Calls;

        await Assert.ThrowsAsync<InvalidOperationException>(() => renewal
            ? client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(30), Token)
            : client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(30), Token));

        Assert.Equal(signatures, signer.Calls);
        await client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(ProtocolModel.FromJson<DeviceCertificate>(draft)!.GetDeviceId(TestNetwork.Context), client.Device!.GetDeviceId(TestNetwork.Context));
        var input = "resumed identity"u8.ToArray();
        Assert.True(Ed25519.Verify(await client.DeviceManager.SignAsync(input, Token), client.Device.SigningPublicKey.AsSpan(), input));
        Assert.Single(client.DeviceState!.Certificates);
    }

    [Fact]
    public async Task Canceled_establishment_retains_its_publication_before_later_public_renewal()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Relay.Requests.Clear();
        var entered = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return fixture.Relay.Respond(request);
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var establishment = fixture.Client.EstablishAccountAsync(cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            // Signing remains available while setup waits for the relay.
            var input = "during publication"u8.ToArray();
            Assert.True(Ed25519.Verify(await fixture.Client.DeviceManager.SignAsync(input, Token), fixture.Client.Device!.SigningPublicKey.AsSpan(), input));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => establishment);
        }
        var prepared = fixture.Client.Device!.ToJson();
        var request = Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish").Body;
        // The setup task has ended, but an unresolved durable request still protects its certificate.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(30), Token));
        fixture.Relay.Handler = null;
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(prepared, fixture.Client.Device.ToJson());
        var publications = fixture.Relay.Requests.Where(value => value.Method == "device.state.publish").ToArray();
        Assert.Equal(2, publications.Length);
        Assert.All(publications, value => Assert.Equal(request, value.Body));

        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(30), Token);
        Assert.Same(renewed, fixture.Client.Device);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        Assert.Equal(renewed.ToJson(), Assert.Single(fixture.Client.DeviceState!.Certificates).ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_drains_account_authorization_and_restart_reuses_the_device_and_signed_publication(bool disposeClient)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var manager = fixture.Client.DeviceManager;
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish")
            {
                entered.TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        var establishment = fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Task disposal;
        string certificate;
        string publication;
        var input = "identity retained after disposal"u8.ToArray();
        byte[] signature;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            certificate = manager.Local!.ToJson();
            signature = await manager.SignAsync(input, Token);
            publication = Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish").Body!;
            disposal = disposeClient ? fixture.Client.DisposeAsync().AsTask() : manager.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(disposal.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (disposeClient)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => establishment);
        else
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => establishment);
            Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        }
        await using (var db = fixture.Database.Open())
        {
            Assert.Single(await db.AccountEstablishments.ToListAsync(Token));
            Assert.True((await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
        }
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var publicManager = fixture.Client.DeviceManager;
        var stateChanged = false;
        publicManager.DeviceStateChanged += (sender, _) => { Assert.Same(publicManager, sender); stateChanged = true; };

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(certificate, publicManager.Local!.ToJson());
        Assert.Equal(signature, await publicManager.SignAsync(input, Token));
        Assert.Equal(publication, Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish").Body);
        Assert.True(stateChanged);
        Assert.Equal(DeviceAuthorizationState.Authorized, publicManager.GetAuthorizationState(publicManager.Local.GetDeviceId(TestNetwork.Context)));
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.False(await verify.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
    }

    sealed class SwitchableSigner(AccountSigner inner) : IAccountSigner
    {
        public string AccountId => inner.AccountId;
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public bool DeclineCertificate { get; set; }
        public int Calls { get; private set; }

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (DeclineCertificate && System.Text.Encoding.UTF8.GetString(data.Span).Contains("device.cert", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("certificate declined");
            return inner.SignAsync(data, cancellationToken);
        }
    }
}
