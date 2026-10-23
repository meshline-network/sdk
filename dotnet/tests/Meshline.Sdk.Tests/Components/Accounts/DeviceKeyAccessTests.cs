using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class DeviceKeyAccessTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_repeat_checks_each_protected_key_once_and_next_call_checks_again()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var reads = 0;
        fixture.Protector.ReadFailure = _ => { reads++; return null; };

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(2, reads);
        fixture.Relay.Requests.Clear();
        fixture.Protector.ReadFailure = _ => new SecretAccessException(SecretAccessFailure.TemporarilyUnavailable, "locked");
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.LocalKeyAccessRequired, error.Reason);
        Assert.Empty(fixture.Relay.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupted_local_key_between_calls_is_not_hidden_by_the_previous_inspection(bool encryption)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        byte[] original;
        await using (var db = fixture.Database.Open())
        {
            var local = await db.LocalDevices.SingleAsync(Token);
            original = encryption ? local.ProtectedEncryptionKey : local.ProtectedSigningKey;
            if (encryption) local.ProtectedEncryptionKey = [.. original.Reverse()];
            else local.ProtectedSigningKey = [.. original.Reverse()];
            await db.SaveChangesAsync(Token);
        }
        fixture.Relay.Requests.Clear();

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.LocalKeyAccessRequired, error.Reason);
        Assert.Equal(SecretAccessFailure.Unknown, error.KeyIssue);
        Assert.Empty(fixture.Relay.Requests);
        await using var verify = fixture.Database.Open();
        var retained = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(original.Reverse(), encryption ? retained.ProtectedEncryptionKey : retained.ProtectedSigningKey);
        Assert.False(await verify.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));
    }

    [Fact]
    public async Task Public_creation_between_attempts_retains_the_previous_identity_before_authorization()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.resolve"
            ? throw new HttpRequestException("discovery failed") : Task.FromResult(fixture.Relay.Respond(request));
        var discovery = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, discovery.Reason);
        fixture.Relay.Handler = null;
        // The first setup task has ended. A standalone mutation may now run.
        var created = await fixture.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(7), Token);
        var createdId = created.GetDeviceId(TestNetwork.Context);
        var interrupt = true;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method == "device.state.publish")
            {
                await using var db = fixture.Database.Open();
                Assert.Equal(createdId, (await db.AccountEstablishments.SingleAsync(Token)).PreviousLocalDeviceId);
                if (interrupt) throw new HttpRequestException("lost publication response");
            }
            return fixture.Relay.Respond(request);
        };

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
        Assert.NotNull(createdId);
        var prepared = fixture.Client.Device!.ToJson();
        Assert.Equal(createdId, fixture.Client.Device.GetDeviceId(TestNetwork.Context));
        await fixture.ReopenAsync();
        interrupt = false;
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(prepared, fixture.Client.Device!.ToJson());
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Fact]
    public async Task Public_renewal_between_establishment_calls_checks_new_certificate_once()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        var original = fixture.Client.Device!;
        var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        var reads = 0;
        fixture.Protector.ReadFailure = _ => { reads++; return null; };
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(2, reads);
        Assert.Equal(original.GetDeviceId(TestNetwork.Context), renewed.GetDeviceId(TestNetwork.Context));
        Assert.NotEqual(original.ToJson(), renewed.ToJson());
        Assert.Equal(renewed.ToJson(), fixture.Client.Device!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method.EndsWith(".publish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Certificate_expiry_after_preflight_renews_without_reopening_the_protected_keys()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(1) }, Token);
        var original = fixture.Client.Device!;
        var reads = 0;
        fixture.Protector.ReadFailure = _ => { reads++; return null; };
        var advanced = false;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "account.route.resolve" && !advanced)
            {
                advanced = true;
                fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(2, reads);
        Assert.Equal(original.GetDeviceId(TestNetwork.Context), fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.True(fixture.Client.Device.ExpiresAt > original.ExpiresAt);
    }
}
