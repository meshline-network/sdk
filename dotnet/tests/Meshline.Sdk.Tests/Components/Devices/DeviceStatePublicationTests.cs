using Meshline.Models.Client;
using Meshline.Interactions;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;

namespace Meshline.Tests.Components.Devices;

public sealed class DeviceStatePublicationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unfinished_publication_survives_restart_and_reuses_its_signature_until_authoritatively_confirmed(bool lostResponse)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var certificate = await fixture.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(7), Token);
        if (lostResponse)
            fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
                ? throw new HttpRequestException("lost response") : Task.FromResult(fixture.Relay.Respond(request));
        if (lostResponse)
            await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [certificate], cancellationToken: Token));
        else
        {
            var result = await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [certificate], cancellationToken: Token);
            Assert.Equal(DeviceStatePublishStatus.Staged, result.Status);
        }
        var original = Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish").Body;
        var signedState = ProtocolModel.FromJson<AccountDeviceState>(original!)!;
        await using (var db = fixture.Database.Open())
            Assert.True((await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);

        await fixture.Client.DisposeAsync();
        fixture.Relay.Handler = null;
        var signer = new RecordingSigner(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        // A different complete list must not displace an unresolved signed request.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [], cancellationToken: Token));

        var retried = await client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [certificate], cancellationToken: Token);

        Assert.Equal(DeviceStatePublishStatus.Staged, retried.Status);
        Assert.Equal(signedState.Revision, retried.DeviceState.Revision);
        Assert.DoesNotContain(signer.Inputs, input => input.AsSpan().SequenceEqual(signedState.GetSigningInput(TestNetwork.Context)));
        var submissions = fixture.Relay.Requests.Where(value => value.Method == "device.state.publish").ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.All(submissions, value => Assert.Equal(original, value.Body));
        await using (var db = fixture.Database.Open())
        {
            Assert.True((await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
            Assert.Empty(await db.DeviceStates.ToListAsync(Token));
        }

        await client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
        var confirmed = await client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
        Assert.Equal(signedState.ToJson(), confirmed!.ToJson());
        await using var verify = fixture.Database.Open();
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_definitive_rejection_finishes_a_failed_publication(bool expiredStaging)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var previous = fixture.Client.DeviceState!.ToJson();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.publish"
            ? expiredStaging
                ? OfflineRelay.Json(new DeviceStatePublishResponse { Status = DeviceStatePublishStatus.Staged, StagedUntil = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds() - 1 })
                : OfflineRelay.Error("state_conflict")
            : fixture.Relay.Respond(request));

        if (expiredStaging)
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!], cancellationToken: Token));
        else
            await Assert.ThrowsAsync<RelayException>(() => fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!], cancellationToken: Token));

        await using var db = fixture.Database.Open();
        Assert.Equal(expiredStaging, (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
        Assert.Equal(previous, (await db.DeviceStates.SingleAsync(Token)).DocumentJson);
        Assert.Equal(previous, fixture.Client.DeviceState!.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Establishment_prepares_a_new_publication_after_rejection_and_retains_its_device(bool renewal)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        if (renewal)
        {
            await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(1) }, Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        }
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        Assert.Equal("invalid_state", Assert.IsType<RelayException>(error.InnerException).Error.Code);
        var original = ProtocolModel.FromJson<AccountDeviceState>(Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish").Body!)!;
        var device = fixture.Client.Device!.ToJson();
        Guid operationId;
        LocalDeviceRecord prepared;
        await using (var db = fixture.Database.Open())
        {
            var signed = await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token);
            Assert.False(signed.Pending);
            Assert.Equal(original.ToJson(), signed.DocumentJson);
            operationId = (await db.AccountEstablishments.SingleAsync(Token)).OperationId;
            prepared = await db.LocalDevices.SingleAsync(Token);
            Assert.Equal(operationId, (await db.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
        }
        await fixture.Client.DisposeAsync();
        fixture.Relay.Handler = null;
        var signer = new RecordingSigner(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        fixture.Relay.Requests.Clear();

        await client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(device, client.Device!.ToJson());
        Assert.Equal(device, Assert.Single(client.DeviceState!.Certificates).ToJson());
        Assert.Contains(signer.Inputs, input => input.AsSpan().SequenceEqual(original.GetSigningInput(TestNetwork.Context)));
        Assert.DoesNotContain(signer.Inputs, input => input.AsSpan().SequenceEqual(client.Device.GetAccountSigningInput(TestNetwork.Context)));
        Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        if (renewal)
        {
            var methods = fixture.Relay.Requests.Select(value => value.Method).ToList();
            Assert.Contains("device.state.resolve", methods.Take(methods.IndexOf("device.state.publish")));
        }
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Equal(operationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
        var retained = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(prepared.ProtectedSigningKey, retained.ProtectedSigningKey);
        Assert.Equal(prepared.ProtectedEncryptionKey, retained.ProtectedEncryptionKey);
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
    }

    [Fact]
    public async Task Establishment_rejects_a_stale_remote_snapshot_without_overwriting_the_saved_request()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var previous = fixture.Client.DeviceState!;
        var newer = await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        fixture.Relay.Requests.Clear();

        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.resolve"
            ? OfflineRelay.Json(previous) : fixture.Relay.Respond(request));
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        await using var db = fixture.Database.Open();
        var signed = await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token);
        Assert.Equal(newer.DeviceState.ToJson(), signed.DocumentJson);
        Assert.False(signed.Pending);
    }

    [Fact]
    public async Task Automatic_publication_preserves_other_devices_and_advances_the_known_revision()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock).Certificate;
        var previous = await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, other], cancellationToken: Token);
        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(30), Token);

        var published = await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);

        Assert.Equal(DeviceStatePublishStatus.Accepted, published.Status);
        Assert.Equal(previous.DeviceState.Revision + 1, published.DeviceState.Revision);
        Assert.Equal(2, published.DeviceState.Certificates.Length);
        Assert.Contains(published.DeviceState.Certificates, value => value.ToJson() == other.ToJson());
        Assert.Contains(published.DeviceState.Certificates, value => value.ToJson() == renewed.ToJson());
    }

    sealed class RecordingSigner(AccountSigner inner) : IAccountSigner
    {
        public string AccountId => inner.AccountId;
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public List<byte[]> Inputs { get; } = [];

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Inputs.Add(data.ToArray());
            return inner.SignAsync(data, cancellationToken);
        }
    }
}
