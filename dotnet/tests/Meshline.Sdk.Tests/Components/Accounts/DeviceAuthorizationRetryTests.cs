using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Meshline.Tests.Support.DeviceCertificateSetup;

namespace Meshline.Tests.Components.Accounts;

public sealed class DeviceAuthorizationRetryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unresolved_device_mutation_between_setup_attempts_is_reconciled_without_replacing_keys(bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            if (request.Method == "profile.publish")
            {
                response.Dispose();
                throw new HttpRequestException("lost profile response");
            }
            return Task.FromResult(response);
        };
        var interrupted = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, interrupted.Reason);
        await using var db = fixture.Database.Open();
        var retained = await db.AccountEstablishments.AsNoTracking().SingleAsync(Token);
        var device = fixture.Client.Device!.ToJson();
        var profile = fixture.Relay.Profiles[fixture.Account.AccountId].Profile.ToJson();

        // Setup has finished with an error before the caller starts this mutation.
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "device.state.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new HttpRequestException("lost mutation response");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.DeviceManager.PublishDeviceStateAsync(
            fixture.Relay.RelayId, [], cancellationToken: Token));
        var pending = await db.SignedRequests.AsNoTracking().SingleAsync(value => value.Method == "device.state.publish", Token);
        Assert.True(pending.Pending);
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        if (!accepted)
        {
            // An unresolved different intent is never overwritten by setup.
            var conflict = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, conflict.Reason);
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
            await using var verifyPending = fixture.Database.Open();
            Assert.Equal(pending.DocumentJson, (await verifyPending.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson);
            Assert.Equal(retained.OperationId, (await verifyPending.AccountEstablishments.SingleAsync(Token)).OperationId);
            await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [], cancellationToken: Token);
            Assert.Equal(pending.DocumentJson, Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish").Body);
        }
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.Equal(profile, fixture.Client.Profile!.ToJson());
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "account.route.publish" or "profile.publish");
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Pending_publication_is_reconciled_before_success(bool compatible, bool committed, bool unfinishedSetup)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var original = fixture.Client.Device!;
        var peer = Certificate(fixture);
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "device.state.publish") return Task.FromResult(fixture.Relay.Respond(request));
            if (committed) fixture.Relay.Respond(request).Dispose();
            throw new HttpRequestException("outcome unknown");
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.DeviceManager.PublishDeviceStateAsync(
            fixture.Relay.RelayId, compatible ? [original] : [peer], cancellationToken: Token));
        string signed;
        await using (var db = fixture.Database.Open())
        {
            signed = (await db.SignedRequests.SingleAsync(row => row.Method == "device.state.publish", Token)).DocumentJson;
            if (unfinishedSetup)
            {
                db.AccountEstablishments.Add(new() { OperationId = Guid.NewGuid(), RelayId = fixture.Relay.RelayId });
                await db.SaveChangesAsync(Token);
            }
        }
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();

        if (!compatible && !committed)
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, error.Reason);
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
            await using var blocked = fixture.Database.Open();
            var pending = await blocked.SignedRequests.SingleAsync(row => row.Method == "device.state.publish", Token);
            Assert.True(pending.Pending);
            Assert.Equal(signed, pending.DocumentJson);
            Assert.Equal(original.ToJson(), fixture.Client.Device!.ToJson());
            return;
        }

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        await using var verify = fixture.Database.Open();
        Assert.False((await verify.SignedRequests.SingleAsync(row => row.Method == "device.state.publish", Token)).Pending);
        Assert.Equal(original.ToJson(), fixture.Client.Device!.ToJson());
        if (compatible && !committed)
            Assert.Equal(signed, ProtocolModel.FromJson<AccountDeviceState>(
                Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish").Body!)!.ToJson());
        if (!compatible)
            Assert.Contains(fixture.Client.DeviceState!.Certificates, certificate => certificate.ToJson() == peer.ToJson());
        Assert.Contains(fixture.Client.DeviceState!.Certificates, certificate => certificate.ToJson() == original.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renewal_activation_survives_interrupted_device_session_creation(bool reopen)
    {
        using var time = Clock.Use(new ManualClock());
        var fixture = new TestClient();
        try
        {
            await fixture.InitializeAsync(false);
            await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
            var oldId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
            var interrupted = false;
            EventHandler interrupt = (_, _) =>
            {
                if (interrupted || fixture.Client.Device!.ExpiresAt <= fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds()) return;
                interrupted = true;
                throw new InvalidOperationException("interrupted device-session creation");
            };
            fixture.Pool.PoolChanged += interrupt;
            fixture.Relay.Requests.Clear();
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            fixture.Pool.PoolChanged -= interrupt;
            Assert.True(interrupted);
            string activatedId;
            byte[] key;
            await using (var db = fixture.Database.Open())
            {
                var local = await db.LocalDevices.SingleAsync(Token);
                activatedId = local.DeviceId;
                key = local.ProtectedSigningKey;
                Assert.Equal(oldId, activatedId);
                Assert.Equal(oldId, (await db.AccountEstablishments.SingleAsync(Token)).PreviousLocalDeviceId);
            }
            Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish");
            if (reopen) await fixture.ReopenAsync();
            var reads = 0;
            fixture.Protector.ReadFailure = _ => { reads++; return null; };
            fixture.Relay.Requests.Clear();

            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
            Assert.Equal(2, reads);
            Assert.Equal(activatedId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
            Assert.Single(fixture.Client.DeviceState!.Certificates);
            await using var verify = fixture.Database.Open();
            Assert.Equal(key, (await verify.LocalDevices.SingleAsync(Token)).ProtectedSigningKey);
            Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Expired_certificate_reconciles_unknown_publication_before_renewing(bool initialAccount, bool committed)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var peer = initialAccount ? null : fixture.Relay.AddPeer(fixture.Account).Certificate;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "device.state.publish") return Task.FromResult(fixture.Relay.Respond(request));
            if (committed) fixture.Relay.Respond(request).Dispose();
            throw new HttpRequestException("Publication outcome unknown.");
        };
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(
            new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token));
        var original = fixture.Client.Device!;
        string pending;
        byte[] signingKey;
        byte[] encryptionKey;
        await using (var db = fixture.Database.Open())
        {
            pending = (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson;
            var local = await db.LocalDevices.SingleAsync(Token);
            signingKey = await fixture.Protector.UnprotectAsync(local.ProtectedSigningKey,
                $"Meshline/device-signing/v1/{TestNetwork.Context}/{fixture.Account.AccountId}/{local.DeviceId}", Token);
            encryptionKey = await fixture.Protector.UnprotectAsync(local.ProtectedEncryptionKey,
                $"Meshline/device-encryption/v1/{TestNetwork.Context}/{fixture.Account.AccountId}/{local.DeviceId}", Token);
        }
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        // Unknown is still unknown: another failed replay must retain both the
        // exact submitted certificate and its original keys, even after expiry.
        if (!committed)
        {
            await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
            Assert.Equal(original.ToJson(), fixture.Client.Device!.ToJson());
            Assert.All(fixture.Relay.Requests.Where(value => value.Method == "device.state.publish"),
                request => Assert.Equal(pending, request.Body));
            await using var db = fixture.Database.Open();
            Assert.Equal(pending, (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson);
        }
        fixture.Relay.Handler = null;
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        var renewed = fixture.Client.Device!;
        Assert.Equal(original.GetDeviceId(TestNetwork.Context), renewed.GetDeviceId(TestNetwork.Context));
        Assert.True(renewed.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds());
        Assert.Contains(fixture.Client.DeviceState!.Certificates, value => value.ToJson() == renewed.ToJson());
        if (peer is not null)
            Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.ToJson() == peer.ToJson());
        await using var verify = fixture.Database.Open();
        var stored = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(signingKey, await fixture.Protector.UnprotectAsync(stored.ProtectedSigningKey,
            $"Meshline/device-signing/v1/{TestNetwork.Context}/{fixture.Account.AccountId}/{stored.DeviceId}", Token));
        Assert.Equal(encryptionKey, await fixture.Protector.UnprotectAsync(stored.ProtectedEncryptionKey,
            $"Meshline/device-encryption/v1/{TestNetwork.Context}/{fixture.Account.AccountId}/{stored.DeviceId}", Token));
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        if (initialAccount || !committed)
            Assert.Contains(fixture.Relay.Requests, value => value.Method == "device.state.publish" && value.Body == pending);
    }

    [Fact]
    public async Task Renewal_expiring_during_submission_stops_and_preserves_keys_for_explicit_retry()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.AddPeer(fixture.Account);
        fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
            ? throw new HttpRequestException("Publication outcome unknown.")
            : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(
            new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token));
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "device.state.publish") fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: cancellation.Token));

        Assert.Equal(AccountEstablishmentReason.AuthorizationNotYetEffective, error.Reason);
        Assert.Equal(2, fixture.Relay.Requests.Count(value => value.Method == "device.state.publish"));
        Assert.Equal(deviceId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        fixture.Relay.Handler = null;
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(deviceId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.True(fixture.Client.Device.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Expired_publication_replay_preserves_the_conflict_budget_and_concurrent_devices()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var peer = fixture.Relay.AddPeer(fixture.Account).Certificate;
        fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
            ? throw new HttpRequestException("Publication outcome unknown.")
            : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(
            new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token));
        var original = fixture.Client.Device!;
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var additions = new[] { Certificate(fixture), Certificate(fixture) };
        var conflicts = 0;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "device.state.publish" && conflicts < additions.Length)
            {
                var submitted = ProtocolModel.FromJson<AccountDeviceState>(request.Body!)!;
                if (submitted.Certificates.Any(value => value.GetDeviceId(TestNetwork.Context) == original.GetDeviceId(TestNetwork.Context)
                    && value.ExpiresAt > original.ExpiresAt))
                {
                    var current = fixture.Relay.Devices[fixture.Account.AccountId];
                    var concurrent = current with
                    {
                        Revision = current.Revision + 1,
                        Certificates = current.Certificates.Add(additions[conflicts++]),
                        AccountSignature = []
                    };
                    fixture.Relay.Devices[fixture.Account.AccountId] = concurrent with
                    {
                        AccountSignature = [.. fixture.Account.Sign(concurrent.GetSigningInput(TestNetwork.Context))]
                    };
                }
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(2, conflicts);
        Assert.Equal(4, fixture.Relay.Requests.Count(value => value.Method == "device.state.publish"));
        Assert.Equal(original.GetDeviceId(TestNetwork.Context), fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(4, fixture.Client.DeviceState!.Certificates.Length);
        foreach (var retained in additions.Prepend(peer))
            Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.ToJson() == retained.ToJson());
    }
}
