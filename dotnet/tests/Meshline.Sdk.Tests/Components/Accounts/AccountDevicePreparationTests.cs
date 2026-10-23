using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountDevicePreparationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_keys_after_activation_cannot_replace_the_device_of_an_unresolved_publication(bool missingRow)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
            ? throw new HttpRequestException("lost response") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        LocalDeviceRecord prepared;
        string pending;
        await using (var db = fixture.Database.Open())
        {
            prepared = await db.LocalDevices.SingleAsync(Token);
            pending = (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson;
            var receipt = await db.DevicePreparations.SingleAsync(Token);
            Assert.Equal((await db.AccountEstablishments.SingleAsync(Token)).OperationId, receipt.ActivatedOperationId);
            Assert.Null(receipt.OperationId);
            Assert.Null(receipt.DraftCertificateJson);
            Assert.Null(receipt.ProtectedSigningKey);
            Assert.Null(receipt.ProtectedEncryptionKey);
            if (missingRow) await db.LocalDevices.ExecuteDeleteAsync(Token);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        if (!missingRow)
            fixture.Protector.ReadFailure = _ => new SecretAccessException(SecretAccessFailure.PermanentlyLost, "lost");
        fixture.Relay.Requests.Clear();

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.LocalKeyAccessRequired, error.Reason);
        Assert.Equal(SecretAccessFailure.PermanentlyLost, error.KeyIssue);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        await using (var db = fixture.Database.Open())
        {
            var local = await db.LocalDevices.SingleOrDefaultAsync(Token);
            if (missingRow)
                Assert.Null(local);
            else
            {
                Assert.NotNull(local);
                Assert.Equal(prepared.CertificateJson, local.CertificateJson);
                Assert.Equal(prepared.ProtectedSigningKey, local.ProtectedSigningKey);
                Assert.Equal(prepared.ProtectedEncryptionKey, local.ProtectedEncryptionKey);
            }
            Assert.Equal(pending, (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson);
            Assert.False(await db.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));

        }
        if (missingRow) return;
        // If the original secret becomes accessible again, resume the exact publication.
        fixture.Protector.ReadFailure = null;
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(prepared.DeviceId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        var request = Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        Assert.Equal(pending, ProtocolModel.FromJson<AccountDeviceState>(request.Body!)!.ToJson());
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("renewal")]
    public async Task Failed_activation_keeps_draft_and_original_device_atomic_then_resumes_after_restart(string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        if (scenario != "initial")
            await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
        LocalDeviceRecord? original;
        await using (var db = fixture.Database.Open())
            original = await db.LocalDevices.SingleOrDefaultAsync(Token);
        if (scenario == "renewal")
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await using (var db = fixture.Database.Open())
        {
            // Fail the activation transaction after it has started writing, leaving
            // the independently committed draft available for the next process.
            var trigger = scenario == "renewal"
                ? "CREATE TRIGGER fail_activation AFTER UPDATE ON LocalDevice BEGIN SELECT RAISE(ABORT, 'activation interrupted'); END;"
                : "CREATE TRIGGER fail_activation AFTER UPDATE OF DeviceId ON DatabaseBinding BEGIN SELECT RAISE(ABORT, 'activation interrupted'); END;";
            await db.Database.ExecuteSqlRawAsync(trigger, Token);
        }
        fixture.Relay.Requests.Clear();

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        DevicePreparationRecord draft;
        await using (var db = fixture.Database.Open())
        {
            draft = await db.DevicePreparations.SingleAsync(Token);
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(setup.OperationId, draft.OperationId);
            Assert.Equal(original?.DeviceId, (await db.Bindings.SingleAsync(Token)).DeviceId);
            Assert.Equal(original?.CertificateJson, (await db.LocalDevices.SingleOrDefaultAsync(Token))?.CertificateJson);

            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_activation;", Token);
        }
        Assert.Equal(original?.CertificateJson, fixture.Client.Device?.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(Assert.IsType<string>(draft.DraftCertificateJson))!;

        await fixture.ReopenAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(certificate.GetDeviceId(TestNetwork.Context), fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(certificate.GetAccountSigningInput(TestNetwork.Context), fixture.Client.Device.GetAccountSigningInput(TestNetwork.Context));
        await using var verify = fixture.Database.Open();
        var local = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(draft.ProtectedSigningKey, local.ProtectedSigningKey);
        Assert.Equal(draft.ProtectedEncryptionKey, local.ProtectedEncryptionKey);
        Assert.Equal(local.DeviceId, (await verify.Bindings.SingleAsync(Token)).DeviceId);
        Assert.False(await verify.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Single(fixture.Client.DeviceState!.Certificates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_renewal_retains_activation_receipt_and_original_keys(bool newOperation)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) => !newOperation && request.Method == "profile.resolve"
            ? throw new HttpRequestException("interrupted profile") : Task.FromResult(fixture.Relay.Respond(request));
        var setup = fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
        if (newOperation) await setup;
        else await Assert.ThrowsAsync<AccountEstablishmentException>(() => setup);
        var activatedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);

        Guid? activatedOperationId;
        await using (var db = fixture.Database.Open())
        {
            activatedOperationId = (await db.DevicePreparations.SingleAsync(Token)).ActivatedOperationId;
            Assert.NotNull(activatedOperationId);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_activation AFTER UPDATE ON LocalDevice BEGIN SELECT RAISE(ABORT, 'activation interrupted'); END;", Token);
        }
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        DevicePreparationRecord pending;
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            var interrupted = await db.AccountEstablishments.SingleAsync(Token);
            operationId = interrupted.OperationId;
            pending = await db.DevicePreparations.SingleAsync(Token);
            Assert.Equal(operationId, pending.OperationId);
            Assert.Equal(activatedOperationId, pending.ActivatedOperationId);
            Assert.Equal(newOperation, activatedOperationId != operationId);
            Assert.Equal(newOperation ? activatedId : null, interrupted.PreviousLocalDeviceId);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_activation;", Token);
        }
        var draft = ProtocolModel.FromJson<DeviceCertificate>(Assert.IsType<string>(pending.DraftCertificateJson))!;
        await fixture.ReopenAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(activatedId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(draft.GetAccountSigningInput(TestNetwork.Context), fixture.Client.Device.GetAccountSigningInput(TestNetwork.Context));
        await using var verify = fixture.Database.Open();
        var receipt = await verify.DevicePreparations.SingleAsync(Token);
        Assert.Equal(operationId, receipt.ActivatedOperationId);
        Assert.Null(receipt.OperationId);
        Assert.Null(receipt.DraftCertificateJson);
        Assert.Null(receipt.ProtectedSigningKey);
        Assert.Null(receipt.ProtectedEncryptionKey);
        var local = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(pending.ProtectedSigningKey, local.ProtectedSigningKey);
        Assert.Equal(pending.ProtectedEncryptionKey, local.ProtectedEncryptionKey);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Empty(await verify.Contacts.ToListAsync(Token));

    }
}
