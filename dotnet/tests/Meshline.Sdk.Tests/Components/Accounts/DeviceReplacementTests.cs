using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text;
using static Meshline.Tests.Support.DeviceCertificateSetup;

namespace Meshline.Tests.Components.Accounts;

public sealed class DeviceReplacementTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_cannot_change_after_unknown_submission_and_omitted_choice_resumes_original(bool committed)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var first = Certificate(fixture);
        var second = Certificate(fixture);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, first, second], cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        var firstId = first.GetDeviceId(TestNetwork.Context);
        var secondId = second.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "device.state.publish") return Task.FromResult(fixture.Relay.Respond(request));
            if (committed) fixture.Relay.Respond(request).Dispose();
            throw new HttpRequestException("unknown replacement");
        };
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token));
        string signed;
        await using (var db = fixture.Database.Open())
            signed = (await db.SignedRequests.SingleAsync(row => row.Method == "device.state.publish", Token)).DocumentJson;
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token));

        Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, error.Reason);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        await using (var db = fixture.Database.Open())
            Assert.Equal(firstId, (await db.AccountEstablishments.SingleAsync(Token)).ReplaceDeviceId);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, certificate => certificate.GetDeviceId(TestNetwork.Context) == firstId);
        Assert.Contains(fixture.Client.DeviceState.Certificates, certificate => certificate.GetDeviceId(TestNetwork.Context) == secondId);
        if (!committed)
            Assert.Equal(signed, ProtocolModel.FromJson<AccountDeviceState>(
                Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish").Body!)!.ToJson());
    }

    [Fact]
    public async Task Changed_replacement_is_retained_before_publication_and_failed_save_preserves_the_old_choice()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var local = fixture.Client.Device!;
        var first = Certificate(fixture);
        var second = Certificate(fixture);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [first, second], cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        var firstId = first.GetDeviceId(TestNetwork.Context);
        var secondId = second.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        var rejectedAuthorization = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token));
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, rejectedAuthorization.Reason);
        Guid operationId;
        string rejected;
        await using (var db = fixture.Database.Open())
        {
            operationId = (await db.AccountEstablishments.SingleAsync(Token)).OperationId;
            rejected = (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson;
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_choice BEFORE UPDATE OF PendingReplaceDeviceId ON AccountEstablishment WHEN NEW.PendingReplaceDeviceId IS NOT NULL BEGIN SELECT RAISE(ABORT, 'choice interrupted'); END;", Token);
        }
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token));

        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(operationId, setup.OperationId);
            Assert.Equal(firstId, setup.ReplaceDeviceId);
            Assert.False(setup.DeviceAuthorizationConfirmed);
            Assert.Equal(rejected, (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_choice;", Token);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method == "device.state.publish")
            {
                await using var db = fixture.Database.Open();
                var setup = await db.AccountEstablishments.SingleAsync(Token);
                Assert.Equal(operationId, setup.OperationId);
                Assert.Equal(firstId, setup.ReplaceDeviceId);
                Assert.Equal(secondId, setup.PendingReplaceDeviceId);
            }
            return fixture.Relay.Respond(request);
        };

        await fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token);

        Assert.Equal(local.ToJson(), fixture.Client.Device!.ToJson());
        Assert.Contains(fixture.Client.DeviceState!.Certificates, certificate => certificate.GetDeviceId(TestNetwork.Context) == firstId);
        Assert.DoesNotContain(fixture.Client.DeviceState.Certificates, certificate => certificate.GetDeviceId(TestNetwork.Context) == secondId);
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_replacement_cannot_change_when_later_setup_or_confirmation_save_is_interrupted(bool failConfirmationSave)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var first = Certificate(fixture);
        var second = Certificate(fixture);
        var local = fixture.Client.Device!;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [local, first, second], cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        var firstId = first.GetDeviceId(TestNetwork.Context);
        var secondId = second.GetDeviceId(TestNetwork.Context);
        if (failConfirmationSave)
        {
            await using var db = fixture.Database.Open();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_confirmation BEFORE UPDATE OF DeviceAuthorizationConfirmed ON AccountEstablishment BEGIN SELECT RAISE(ABORT, 'confirmation interrupted'); END;", Token);
            await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_confirmation;", Token);
        }
        else
        {
            fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
                ? throw new HttpRequestException("profile unavailable") : Task.FromResult(fixture.Relay.Respond(request));
            var interrupted = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token));
            Assert.Equal("profile unavailable", Assert.IsType<HttpRequestException>(interrupted.InnerException).Message);
            fixture.Relay.Handler = null;
        }
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token));

        Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, error.Reason);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "device.state.publish");
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(firstId, setup.ReplaceDeviceId);
            Assert.Equal(!failConfirmationSave, setup.DeviceAuthorizationConfirmed);
        }
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(local.ToJson(), fixture.Client.Device!.ToJson());
        Assert.Contains(fixture.Client.DeviceState!.Certificates, certificate => certificate.GetDeviceId(TestNetwork.Context) == secondId);
        Assert.DoesNotContain(fixture.Client.DeviceState.Certificates, certificate => certificate.GetDeviceId(TestNetwork.Context) == firstId);
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("accepted", false)]
    [InlineData("accepted", true)]
    [InlineData("checkpoint", false)]
    [InlineData("checkpoint", true)]
    public async Task Changed_selection_replays_its_own_effect_before_the_workflow_accepts_it(string interruption, bool repeatChoice)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var first = Certificate(fixture);
        var second = Certificate(fixture);
        var local = fixture.Client.Device!;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [first, second], cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        var firstId = first.GetDeviceId(TestNetwork.Context);
        var secondId = second.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token));
        string? originalExecution;
        await using (var db = fixture.Database.Open())
            originalExecution = (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).OperationKey;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "device.state.publish" || interruption == "checkpoint")
                return Task.FromResult(fixture.Relay.Respond(request));
            if (interruption == "accepted") fixture.Relay.Respond(request).Dispose();
            throw new HttpRequestException("replacement interrupted");
        };
        if (interruption == "checkpoint")
        {
            await using var db = fixture.Database.Open();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_confirmation BEFORE UPDATE OF DeviceAuthorizationConfirmed ON AccountEstablishment BEGIN SELECT RAISE(ABORT, 'checkpoint interrupted'); END;", Token);
            await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_confirmation;", Token);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token));
            Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
        }
        string signed;
        string? proposedExecution;
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(firstId, setup.ReplaceDeviceId);
            Assert.Equal(secondId, setup.PendingReplaceDeviceId);
            Assert.False(setup.DeviceAuthorizationConfirmed);
            var request = await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token);
            signed = request.DocumentJson;
            proposedExecution = request.OperationKey;
            Assert.NotNull(proposedExecution);
            Assert.NotEqual(originalExecution, proposedExecution);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        var keyReads = 0;
        fixture.Protector.ReadFailure = _ => { keyReads++; return null; };

        await fixture.Client.EstablishAccountAsync(repeatChoice ? new() { ReplaceDeviceId = secondId } : null, Token);

        Assert.Equal(2, keyReads);
        Assert.Equal(local.ToJson(), fixture.Client.Device!.ToJson());
        Assert.Contains(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == firstId);
        Assert.DoesNotContain(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == secondId);
        var repeated = fixture.Relay.Requests.Where(value => value.Method == "device.state.publish").ToArray();
        if (interruption == "unknown")
            Assert.Equal(signed, ProtocolModel.FromJson<AccountDeviceState>(Assert.Single(repeated).Body!)!.ToJson());
        else
            Assert.Empty(repeated);
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Equal(proposedExecution, (await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).OperationKey);
    }

    [Fact]
    public async Task Interrupted_rejection_of_a_new_choice_preserves_the_original_pending_request()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var first = Certificate(fixture);
        var second = Certificate(fixture);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, first, second], cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        var firstId = first.GetDeviceId(TestNetwork.Context);
        var secondId = second.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
            ? throw new HttpRequestException("unknown original request") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token));
        string signed;
        await using (var db = fixture.Database.Open())
        {
            signed = (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson;
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_rejection BEFORE UPDATE OF PendingReplaceDeviceId ON AccountEstablishment WHEN NEW.PendingReplaceDeviceId IS NULL BEGIN SELECT RAISE(ABORT, 'rejection interrupted'); END;", Token);
        }
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token));

        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(firstId, setup.ReplaceDeviceId);
            Assert.Equal(secondId, setup.PendingReplaceDeviceId);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_rejection;", Token);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(signed, ProtocolModel.FromJson<AccountDeviceState>(Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish").Body!)!.ToJson());
        Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == firstId);
        Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == secondId);
    }

    [Fact]
    public async Task Declined_proposed_publication_allows_another_explicit_choice_after_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var first = Certificate(fixture);
        var second = Certificate(fixture);
        var third = Certificate(fixture);
        var local = fixture.Client.Device!.ToJson();
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [first, second, third], cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        var firstId = first.GetDeviceId(TestNetwork.Context);
        var secondId = second.GetDeviceId(TestNetwork.Context);
        var thirdId = third.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token));
        fixture.Relay.Handler = null;
        await fixture.Client.DisposeAsync();
        fixture.Relay.Requests.Clear();
        await using (var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Protector, new DecliningDeviceStateSigner(fixture.Account)))
        {
            await client.InitializeAsync(Token);

            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token));

            Assert.Equal(AccountEstablishmentReason.SignatureDeclined, error.Reason);
            Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
            await using var db = fixture.Database.Open();
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(secondId, setup.ReplaceDeviceId);
            Assert.Null(setup.PendingReplaceDeviceId);
            Assert.False(setup.DeviceAuthorizationConfirmed);
        }
        await fixture.ReopenAsync();

        await fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = thirdId }, Token);

        Assert.Equal(local, fixture.Client.Device!.ToJson());
        Assert.Contains(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == firstId);
        Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == secondId);
        Assert.DoesNotContain(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == thirdId);
    }

    [Fact]
    public async Task Submitted_proposal_can_change_after_a_definitive_replay_rejection()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var first = Certificate(fixture);
        var second = Certificate(fixture);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [first, second], cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        var firstId = first.GetDeviceId(TestNetwork.Context);
        var secondId = second.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token));
        fixture.Relay.Handler = (request, _) => request.Method == "device.state.publish"
            ? throw new HttpRequestException("unknown proposal") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = secondId }, Token));
        await fixture.ReopenAsync();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.publish"
            ? OfflineRelay.Error("invalid_state") : fixture.Relay.Respond(request));

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(secondId, setup.ReplaceDeviceId);
            Assert.Null(setup.PendingReplaceDeviceId);
            Assert.False((await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
        }
        fixture.Relay.Handler = null;

        await fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = firstId }, Token);

        Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == firstId);
        Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == secondId);
    }

    [Fact]
    public async Task Revocation_after_profile_interruption_allows_explicit_replacement_at_capacity()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.AddPeer(fixture.Account);
        var old = Certificate(fixture, expired: true);
        var oldId = old.GetDeviceId(TestNetwork.Context);
        var originalState = fixture.Relay.Devices[fixture.Account.AccountId] with { Certificates = [old], AccountSignature = [] };
        fixture.Relay.Devices[fixture.Account.AccountId] = originalState with
        { AccountSignature = [.. fixture.Account.Sign(originalState.GetSigningInput(TestNetwork.Context))] };
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw new HttpRequestException("Profile temporarily unavailable.")
            : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        var enabled = fixture.Client.Device!;
        var enabledId = enabled.GetDeviceId(TestNetwork.Context);
        Assert.NotEqual(oldId, enabledId);
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            var setup = await db.AccountEstablishments.SingleAsync(Token);
            Assert.True(setup.DeviceAuthorizationConfirmed);
            operationId = setup.OperationId;
        }
        fixture.Relay.Handler = null;
        // The device confirmed by this operation is subsequently revoked and
        // another device fills the last slot before profile initialization resumes.
        var certificates = Enumerable.Range(0, 7).Select(_ => Certificate(fixture)).Prepend(old).ToArray();
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, certificates, cancellationToken: Token);
        await fixture.ReopenAsync();
        var selection = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.DeviceSelectionRequired, selection.Reason);
        await using (var db = fixture.Database.Open())
            Assert.Equal(operationId, (await db.AccountEstablishments.SingleAsync(Token)).OperationId);

        await fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = oldId }, Token);

        Assert.Equal(enabled.ToJson(), fixture.Client.Device!.ToJson());
        var state = fixture.Client.DeviceState!;
        Assert.Equal(8, state.Certificates.Length);
        Assert.Contains(state.Certificates, value => value.GetDeviceId(TestNetwork.Context) == enabledId);
        Assert.DoesNotContain(state.Certificates, value => value.GetDeviceId(TestNetwork.Context) == oldId);
        foreach (var certificate in certificates.Skip(1))
            Assert.Contains(state.Certificates, value => value.ToJson() == certificate.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_replacement_then_revocation_can_choose_again_and_resume_unknown_submission(bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.AddPeer(fixture.Account);
        var old = Certificate(fixture, expired: true);
        var oldId = old.GetDeviceId(TestNetwork.Context);
        var originalState = fixture.Relay.Devices[fixture.Account.AccountId] with { Certificates = [old], AccountSignature = [] };
        fixture.Relay.Devices[fixture.Account.AccountId] = originalState with
        { AccountSignature = [.. fixture.Account.Sign(originalState.GetSigningInput(TestNetwork.Context))] };
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw new HttpRequestException("Profile temporarily unavailable.") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = oldId }, Token));
        var enabled = fixture.Client.Device!;
        var enabledId = enabled.GetDeviceId(TestNetwork.Context);
        Assert.NotEqual(oldId, enabledId);
        fixture.Relay.Handler = null;
        // The first removal remains effective, but the new device is subsequently
        // revoked and eight other devices occupy the account's available slots.
        var others = Enumerable.Range(0, 8).Select(_ => Certificate(fixture)).ToArray();
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, others, cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        await fixture.ReopenAsync();
        var required = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.DeviceSelectionRequired, required.Reason);
        Assert.Equal(8, required.DeviceSelection!.Devices.Count);
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "device.state.publish") return Task.FromResult(fixture.Relay.Respond(request));
            if (accepted) fixture.Relay.Respond(request).Dispose();
            throw new HttpRequestException("Replacement outcome unknown.");
        };
        var selectedId = others[0].GetDeviceId(TestNetwork.Context);
        var interrupted = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = selectedId }, Token));
        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, interrupted.Reason);
        var original = Assert.Single(fixture.Relay.Requests, request => request.Method == "device.state.publish").Body;
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        var conflict = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            ReplaceDeviceId = others[1].GetDeviceId(TestNetwork.Context)
        }, Token));
        Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, conflict.Reason);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(enabled.ToJson(), fixture.Client.Device!.ToJson());
        Assert.Equal(8, fixture.Client.DeviceState!.Certificates.Length);
        Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == enabledId);
        Assert.DoesNotContain(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
        Assert.DoesNotContain(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == oldId);
        foreach (var other in others.Skip(1))
            Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.ToJson() == other.ToJson());
        Assert.All(fixture.Relay.Requests.Where(request => request.Method == "device.state.publish"), request => Assert.Equal(original, request.Body));
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    sealed class DecliningDeviceStateSigner(AccountSigner inner) : IAccountSigner
    {
        public string AccountId => inner.AccountId;
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            Encoding.UTF8.GetString(data.Span).Contains("meshline.account.device.state", StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException("Device-state signing declined") : inner.SignAsync(data, cancellationToken);
    }
}
