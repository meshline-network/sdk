using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using static Meshline.Tests.Support.DeviceCertificateSetup;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountEstablishmentTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Establishment_snapshots_options_and_nested_profile_before_asynchronous_work()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var avatar = new ContentReference
        {
            Uri = "https://example.com/avatar.png",
            Hash = "sha256:" + new string('A', 43),
            ContentType = "image/png",
            Size = 10
        };
        var profile = new AccountInitialProfile
        {
            Nickname = "Alice",
            Bio = "Original",
            Avatar = avatar,
            PublicDiscovery = true
        };
        var options = new AccountEstablishmentOptions
        {
            RelayId = fixture.Relay.RelayId,
            CertificateValidity = TimeSpan.FromDays(7),
            RouteValidity = TimeSpan.FromDays(14),
            InitialProfile = profile
        };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return fixture.Relay.Respond(request);
        };

        var establishment = fixture.Client.EstablishAccountAsync(options, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            options.RelayId = "invalid";
            options.CertificateValidity = TimeSpan.Zero;
            options.RouteValidity = TimeSpan.Zero;
            options.ReplaceDeviceId = "invalid";
            profile.Nickname = "Changed";
            profile.Bio = "Changed";
            profile.Avatar = null;
            profile.PublicDiscovery = false;
            options.InitialProfile = new() { Nickname = "Replacement" };
        }
        finally
        {
            release.TrySetResult();
        }
        await establishment;

        Assert.Equal(fixture.Relay.RelayId, fixture.Client.Route!.RelayId);
        Assert.Equal(7 * 86400, fixture.Client.Device!.ExpiresAt - fixture.Client.Device.NotBefore);
        Assert.Equal(14 * 86400, fixture.Client.Route.ExpiresAt - fixture.Client.Route.UpdatedAt);
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        Assert.Equal("Alice", fixture.Client.Profile!.Nickname);
        Assert.Equal("Original", fixture.Client.Profile.Bio);
        Assert.Equal(avatar.ToJson(), fixture.Client.Profile.Avatar!.ToJson());
        Assert.True(fixture.Client.Profile.PublicDiscovery);
    }

    [Fact]
    public async Task Initial_setup_creates_profile_without_starting_runtime_and_repeat_needs_no_signer()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new()
        {
            RelayId = fixture.Relay.RelayId,
            InitialProfile = new() { Nickname = "Alice", Bio = "Retained", PublicDiscovery = true }
        }, Token);
        Assert.Equal(ComponentState.Stopped, fixture.Client.LifecycleState);
        Assert.Equal("Alice", fixture.Client.Profile!.Nickname);
        var original = fixture.Client.Device!.ToJson();
        await fixture.Client.DisposeAsync();
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector);
        await client.InitializeAsync(Token);
        fixture.Relay.Requests.Clear();
        await client.EstablishAccountAsync(new() { InitialProfile = new() { Nickname = "Ignored" } }, Token);
        Assert.Equal(original, client.Device!.ToJson());
        Assert.Equal("Alice", client.Profile!.Nickname);
        Assert.True(client.Profile.PublicDiscovery);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method is "auth.account.verify" or "device.state.publish" or "account.route.publish" or "profile.publish");
    }

    [Fact]
    public async Task Existing_account_on_new_install_adds_one_device_and_preserves_profile_and_route()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "Existing", Bio = "Keep", PublicDiscovery = true }, Token);
        var profile = fixture.Client.Profile!.ToJson();
        var route = fixture.Client.Route!.ToJson();
        var oldId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using var pool = fixture.Relay.Pool(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), database.Options, pool, fixture.Protector, fixture.Account);
        await client.InitializeAsync(Token);
        fixture.Relay.Requests.Clear();
        await client.EstablishAccountAsync(new() { InitialProfile = new() { Nickname = "Do not overwrite" } }, Token);
        Assert.Equal(2, client.DeviceState!.Certificates.Length);
        Assert.Contains(client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == oldId);
        Assert.Equal(profile, client.Profile!.ToJson());
        Assert.Equal(route, client.Route!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method is "account.route.publish" or "profile.publish");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Expiry_renews_only_the_invalid_part_using_original_identity(bool certificateExpires)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new()
        {
            RelayId = fixture.Relay.RelayId,
            CertificateValidity = TimeSpan.FromSeconds(certificateExpires ? 10 : 1000),
            RouteValidity = TimeSpan.FromSeconds(certificateExpires ? 1000 : 10)
        }, Token);
        var id = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var deviceRevision = fixture.Client.DeviceState!.Revision;
        var routeRevision = fixture.Client.Route!.Revision;
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(id, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        Assert.Equal(deviceRevision + (certificateExpires ? 1 : 0), fixture.Client.DeviceState.Revision);
        Assert.Equal(routeRevision + (certificateExpires ? 0 : 1), fixture.Client.Route!.Revision);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == (certificateExpires ? "account.route.publish" : "device.state.publish"));
    }

    [Fact]
    public async Task Revocation_is_not_repaired_by_start_but_explicit_establishment_reauthorizes_same_keys()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var id = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, 1, []);
        await fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
        fixture.Relay.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.StartAsync(Token));
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method is "device.state.publish" or "account.route.publish");
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(id, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Null(fixture.Client.DeviceState!.ValidateDeviceAuthorization(id, TestNetwork.Context));
    }

    [Theory]
    [InlineData("account.route.resolve")]
    [InlineData("device.state.resolve")]
    public async Task Query_failure_never_recreates_account_or_changes_remote_state(string method)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var state = fixture.Client.DeviceState!.ToJson();
        var device = fixture.Client.Device!.ToJson();
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) => request.Method == method
            ? throw new HttpRequestException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        Assert.Equal(state, fixture.Relay.Devices[fixture.Account.AccountId].ToJson());
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Verb == HttpMethod.Put);
    }

    [Theory]
    [InlineData(SecretAccessFailure.Unknown)]
    [InlineData(SecretAccessFailure.TemporarilyUnavailable)]
    [InlineData(SecretAccessFailure.PermanentlyLost)]
    public async Task Key_access_failure_preserves_database_and_device(SecretAccessFailure failure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var id = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Protector.ReadFailure = _ => new SecretAccessException(failure, "locked");
        fixture.Relay.Requests.Clear();
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.LocalKeyAccessRequired, error.Reason);
        Assert.Equal(failure, error.KeyIssue);
        await using var db = fixture.Database.Open();
        Assert.Equal(id, (await db.LocalDevices.SingleAsync(Token)).DeviceId);
        Assert.Empty(fixture.Relay.Requests);
    }

    [Fact]
    public async Task Missing_local_key_row_preserves_history_and_refuses_automatic_replacement()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var oldId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        await AddHistoryAsync(fixture, oldId);
        await using (var db = fixture.Database.Open()) await db.LocalDevices.ExecuteDeleteAsync(Token);
        await fixture.ReopenAsync();
        Assert.Null(fixture.Client.Device);
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.LocalKeyAccessRequired, error.Reason);
        Assert.Equal(SecretAccessFailure.PermanentlyLost, error.KeyIssue);
        Assert.Null(fixture.Client.Device);
        await using var read = fixture.Database.Open();
        Assert.Single(await read.Messages.ToListAsync(Token));
    }

    [Fact]
    public async Task Full_list_returns_verified_selection_and_replacement_is_one_atomic_publication()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.AddPeer(fixture.Account);
        var original = Certificate(fixture, expired: true);
        var oldId = original.GetDeviceId(TestNetwork.Context);
        var full = new[] { original }.Concat(Enumerable.Range(1, 7).Select(_ => Certificate(fixture))).ToArray();
        fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, 1, full);
        fixture.Relay.Requests.Clear();
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.DeviceSelectionRequired, error.Reason);
        Assert.Equal(8, error.DeviceSelection!.DeviceLimit);
        Assert.Equal(1, error.DeviceSelection.Revision);
        Assert.Equal(8, error.DeviceSelection.Devices.Count);
        Assert.Null(error.DeviceSelection.PreviousLocalDeviceId);
        var previous = Assert.Single(error.DeviceSelection.Devices, value => value.DeviceId == oldId);
        Assert.Equal(full.Select(value => value.ToJson()), error.DeviceSelection.Devices.Select(value => value.Certificate.ToJson()));
        var pendingId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        await fixture.ReopenAsync();
        await fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = previous.DeviceId }, Token);
        Assert.Equal(pendingId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        var request = Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        var published = ProtocolModel.FromJson<AccountDeviceState>(request.Body!)!;
        Assert.Equal(8, published.Certificates.Length);
        Assert.DoesNotContain(published.Certificates, value => value.GetDeviceId(TestNetwork.Context) == oldId);
        Assert.Contains(published.Certificates, value => value.GetDeviceId(TestNetwork.Context) == pendingId);
        Assert.All(full.Skip(1), other => Assert.Contains(published.Certificates, value => value.ToJson() == other.ToJson()));
    }

    [Fact]
    public async Task Selection_after_restart_uses_current_remote_list_and_retained_original_identity()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var original = fixture.Client.Device!;
        var oldId = original.GetDeviceId(TestNetwork.Context);
        // The complete local identity remains intact, but has been revoked.
        var full = Enumerable.Range(0, 8).Select(_ => Certificate(fixture)).ToArray();
        fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, 1, full);
        var first = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.DeviceSelectionRequired, first.Reason);
        var preparedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        byte[] preparedKey;
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            preparedKey = (await db.LocalDevices.SingleAsync(Token)).ProtectedSigningKey;
            operationId = (await db.AccountEstablishments.SingleAsync(Token)).OperationId;
        }
        await fixture.ReopenAsync();
        var removedId = full[^1].GetDeviceId(TestNetwork.Context);
        var concurrent = Certificate(fixture);
        var latest = full.Where(value => value.GetDeviceId(TestNetwork.Context) != removedId).Append(concurrent).ToArray();
        fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, 2, latest);
        fixture.Relay.Requests.Clear();

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.DeviceSelectionRequired, error.Reason);
        Assert.Equal(oldId, error.DeviceSelection!.PreviousLocalDeviceId);
        Assert.Equal(2, error.DeviceSelection.Revision);
        Assert.Equal(latest.Select(value => value.ToJson()), error.DeviceSelection.Devices.Select(value => value.Certificate.ToJson()));
        Assert.Equal(latest.Select(value => value.GetDeviceId(TestNetwork.Context)), error.DeviceSelection.Devices.Select(value => value.DeviceId));
        Assert.DoesNotContain(error.DeviceSelection.Devices, value => value.DeviceId == error.DeviceSelection.PreviousLocalDeviceId);
        Assert.DoesNotContain(error.DeviceSelection.Devices, value => value.DeviceId == preparedId);
        Assert.Equal(preparedId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        await using var verify = fixture.Database.Open();
        Assert.Equal(preparedKey, (await verify.LocalDevices.SingleAsync(Token)).ProtectedSigningKey);
        Assert.Equal(operationId, (await verify.AccountEstablishments.SingleAsync(Token)).OperationId);
        Assert.NotEqual(operationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
    }

    [Fact]
    public async Task Concurrent_addition_filling_last_slot_returns_latest_selection_without_overwriting_it()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var peer = fixture.Relay.AddPeer(fixture.Account);
        var before = new[] { peer.Certificate }.Concat(Enumerable.Range(1, 6).Select(_ => Certificate(fixture))).ToArray();
        fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, 1, before);
        var concurrent = Certificate(fixture);
        var latest = State(fixture, 2, before.Append(concurrent));
        var injected = false;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "device.state.publish" && !injected)
            {
                injected = true;
                fixture.Relay.Devices[fixture.Account.AccountId] = latest;
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.True(injected);
        Assert.Equal(AccountEstablishmentReason.DeviceSelectionRequired, error.Reason);
        Assert.Equal(2, error.DeviceSelection!.Revision);
        Assert.Null(error.DeviceSelection.PreviousLocalDeviceId);
        Assert.Equal(latest.Certificates.Select(value => value.ToJson()), error.DeviceSelection.Devices.Select(value => value.Certificate.ToJson()));
        Assert.Equal(latest.ToJson(), fixture.Relay.Devices[fixture.Account.AccountId].ToJson());
        Assert.DoesNotContain(error.DeviceSelection.Devices, value => value.DeviceId == fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
    }

    [Fact]
    public async Task Fresh_install_cannot_replace_valid_device_without_contact_history()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var peer = fixture.Relay.AddPeer(fixture.Account);
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            ReplaceDeviceId = peer.Certificate.GetDeviceId(TestNetwork.Context)
        }, Token));
        Assert.Equal(AccountEstablishmentReason.ContactContinuityUnverifiable, error.Reason);
        Assert.Equal("contact_history_unavailable", Assert.Single(error.ContactIssues).Code);
        Assert.Single(fixture.Relay.Devices[fixture.Account.AccountId].Certificates);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
    }

    [Fact]
    public async Task Conflict_rereads_remote_state_and_preserves_concurrent_addition_and_removal()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var old = fixture.Relay.AddPeer(fixture.Account);
        var concurrent = Certificate(fixture);
        var inject = true;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "device.state.publish" && inject)
            {
                inject = false;
                fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, 1, [concurrent]);
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(2, fixture.Client.DeviceState!.Revision);
        Assert.Equal(2, fixture.Client.DeviceState.Certificates.Length);
        Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.ToJson() == concurrent.ToJson());
        Assert.DoesNotContain(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == old.Certificate.GetDeviceId(TestNetwork.Context));
        Assert.Equal(2, fixture.Relay.Requests.Count(value => value.Method == "device.state.publish"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_device_submission_reuses_request_or_recognizes_remote_commit(bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.AddPeer(fixture.Account);
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "device.state.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw new HttpRequestException("lost response");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
        var original = Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish").Body;
        var id = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(id, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(accepted ? 1 : 2, fixture.Relay.Requests.Count(value => value.Method == "device.state.publish"));
        Assert.All(fixture.Relay.Requests.Where(value => value.Method == "device.state.publish"), value => Assert.Equal(original, value.Body));
    }

    [Theory]
    [InlineData("device.state.resolve")]
    [InlineData("account.route.resolve")]
    public async Task Confirmation_query_failure_retains_accepted_authorization_and_retry_does_not_republish(string method)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var peer = fixture.Relay.AddPeer(fixture.Account);
        var published = false;
        fixture.Relay.Handler = (request, _) =>
        {
            if (published && request.Method == method)
                throw new HttpRequestException("confirmation unavailable");
            var response = fixture.Relay.Respond(request);
            if (request.Method == "device.state.publish") published = true;
            return Task.FromResult(response);
        };

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.True(published);
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        Assert.IsType<HttpRequestException>(error.InnerException);
        var accepted = fixture.Relay.Devices[fixture.Account.AccountId].ToJson();
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        Assert.Contains(fixture.Relay.Devices[fixture.Account.AccountId].Certificates, value => value.GetDeviceId(TestNetwork.Context) == deviceId);
        Assert.Contains(fixture.Relay.Devices[fixture.Account.AccountId].Certificates, value => value.ToJson() == peer.Certificate.ToJson());
        LocalDeviceRecord local;
        Guid operationId;
        await using (var db = fixture.Database.Open())
        {
            local = await db.LocalDevices.SingleAsync(Token);
            operationId = (await db.AccountEstablishments.SingleAsync(Token)).OperationId;
            Assert.Equal(operationId, (await db.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
            Assert.Equal(accepted, (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(local.CertificateJson, fixture.Client.Device!.ToJson());
        Assert.Equal(accepted, fixture.Client.DeviceState!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method is "device.state.publish" or "account.route.publish");
        await using var verify = fixture.Database.Open();
        var retained = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(local.ProtectedSigningKey, retained.ProtectedSigningKey);
        Assert.Equal(local.ProtectedEncryptionKey, retained.ProtectedEncryptionKey);
        Assert.Equal(operationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
    }

    [Fact]
    public async Task Staged_existing_account_is_not_success()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.AddPeer(fixture.Account);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.publish"
            ? OfflineRelay.Json(new DeviceStatePublishResponse { Status = DeviceStatePublishStatus.Staged, StagedUntil = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds() + 300 })
            : fixture.Relay.Respond(request));
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.AuthorizationNotYetEffective, error.Reason);
        await using var db = fixture.Database.Open();
        Assert.Single(await db.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_account_signing_resumes_the_same_persisted_keys(bool draftExpires)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.DisposeAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var signer = new CallbackSigner(fixture.Account) { BeforeSign = () => cancellation.Cancel() };
        await using (var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector, signer))
        {
            await client.InitializeAsync(Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EstablishAccountAsync(new()
            {
                RelayId = fixture.Relay.RelayId,
                CertificateValidity = TimeSpan.FromSeconds(10)
            }, cancellation.Token));
            Assert.Null(client.Device);
        }
        DevicePreparationRecord preparation;
        await using (var db = fixture.Database.Open())
        {
            preparation = await db.DevicePreparations.SingleAsync(Token);
            Assert.NotEmpty(Assert.IsType<byte[]>(preparation.ProtectedSigningKey));
            Assert.NotEmpty(Assert.IsType<byte[]>(preparation.ProtectedEncryptionKey));
        }
        var draft = ProtocolModel.FromJson<DeviceCertificate>(Assert.IsType<string>(preparation.DraftCertificateJson))!;
        if (draftExpires) fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.ReopenAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(draft.GetDeviceId(TestNetwork.Context), fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        if (draftExpires)
            Assert.True(fixture.Client.Device.ExpiresAt > draft.ExpiresAt);
        else
            Assert.Equal(draft.GetAccountSigningInput(TestNetwork.Context), fixture.Client.Device.GetAccountSigningInput(TestNetwork.Context));
        Assert.Single(fixture.Relay.Devices[fixture.Account.AccountId].Certificates);
        await using var verify = fixture.Database.Open();
        Assert.False(await verify.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));
        if (!draftExpires)
        {
            var local = await verify.LocalDevices.SingleAsync(Token);
            Assert.Equal(preparation.ProtectedSigningKey, local.ProtectedSigningKey);
            Assert.Equal(preparation.ProtectedEncryptionKey, local.ProtectedEncryptionKey);
        }
    }

    [Fact]
    public async Task Concurrent_local_calls_share_one_setup_and_one_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Client.EstablishAccountAsync(new() { RelayId = fixture.Relay.RelayId }, Token)));
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
    }

    [Fact]
    public async Task Queued_calls_resume_an_unknown_submission_with_the_same_operation_and_keys()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var publications = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish" && Interlocked.Increment(ref publications) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                throw new HttpRequestException("lost response");
            }
            return fixture.Relay.Respond(request);
        };
        var first = fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Guid operationId;
        LocalDeviceRecord prepared;
        string pending;
        await using (var db = fixture.Database.Open())
        {
            operationId = (await db.AccountEstablishments.SingleAsync(Token)).OperationId;
            prepared = await db.LocalDevices.SingleAsync(Token);
            pending = (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson;
        }
        var retries = Enumerable.Range(0, 3).Select(_ => fixture.Client.EstablishAccountAsync(cancellationToken: Token)).ToArray();
        Assert.All(retries, task => Assert.False(task.IsCompleted));

        release.TrySetResult();
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => first);
        await Task.WhenAll(retries);

        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
        Assert.Equal(2, publications);
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        Assert.Equal(prepared.DeviceId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.All(fixture.Relay.Requests.Where(value => value.Method == "device.state.publish"),
            request => Assert.Equal(pending, ProtocolModel.FromJson<AccountDeviceState>(request.Body!)!.ToJson()));
        await using var verify = fixture.Database.Open();
        var local = await verify.LocalDevices.SingleAsync(Token);
        Assert.Equal(prepared.ProtectedSigningKey, local.ProtectedSigningKey);
        Assert.Equal(prepared.ProtectedEncryptionKey, local.ProtectedEncryptionKey);
        Assert.Equal(operationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.False(await verify.DevicePreparations.AnyAsync(value => value.OperationId != null, Token));
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
    }

    [Fact]
    public async Task Existing_home_relay_cannot_be_changed_by_establishment_options()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var other = new OfflineRelay("other.test");
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { RelayId = other.RelayId }, Token));
        Assert.Equal(AccountEstablishmentReason.RelayMismatch, error.Reason);
        Assert.Equal(fixture.Relay.RelayId, fixture.Client.Route!.RelayId);
    }

    [Fact]
    public async Task Renewal_at_capacity_keeps_all_eight_device_identities()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
        var full = new[] { fixture.Client.Device! }.Concat(Enumerable.Range(1, 7).Select(_ => Certificate(fixture))).ToArray();
        fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, 1, full);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(full.Select(value => value.GetDeviceId(TestNetwork.Context)).Order(),
            fixture.Client.DeviceState!.Certificates.Select(value => value.GetDeviceId(TestNetwork.Context)).Order());
        Assert.True(fixture.Client.Device!.ExpiresAt > full[0].ExpiresAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reauthorization_completes_without_supplementing_or_sending_contact_grants(bool failDelivery)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var oldId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, fixture.Client.DeviceState!.Revision + 1, []);
        fixture.Relay.Requests.Clear();
        fixture.Relay.Handler = (request, _) => failDelivery && request.Method == "message.send"
            ? throw new HttpRequestException("unavailable") : Task.FromResult(fixture.Relay.Respond(request));

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        var localId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        Assert.Equal(oldId, localId);
        Assert.Contains(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == oldId);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "message.send");
        Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        await using var db = fixture.Database.Open();
        var contact = await db.Contacts.SingleAsync(Token);
        Assert.Contains(localId, ProtocolModel.FromJson<ContactGrant>(contact.GrantToJson!)!.Signatures.Keys);
        Assert.Null(contact.ConfirmedGrantToJson);
        Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
        Assert.Equal(ComponentState.Stopped, fixture.Client.LifecycleState);
    }

    [Fact]
    public async Task Profile_query_failure_after_authorization_preserves_operation_and_retries_without_registration()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw new HttpRequestException("profile unavailable") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { InitialProfile = new() { Nickname = "Kept" } }, Token));
        var id = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "profile.publish");
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(id, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal("Kept", fixture.Client.Profile!.Nickname);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method is "device.state.publish" or "account.route.publish" or "auth.account.verify");
    }

    [Fact]
    public async Task Repeated_remote_conflicts_stop_with_structured_reason_and_never_drop_other_devices()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var old = fixture.Relay.AddPeer(fixture.Account).Certificate;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "device.state.publish")
            {
                var previous = fixture.Relay.Devices[fixture.Account.AccountId];
                fixture.Relay.Devices[fixture.Account.AccountId] = State(fixture, previous.Revision + 1, previous.Certificates);
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.StateConflict, error.Reason);
        Assert.Equal(3, fixture.Relay.Requests.Count(value => value.Method == "device.state.publish"));
        Assert.Equal(old.ToJson(), Assert.Single(fixture.Relay.Devices[fixture.Account.AccountId].Certificates).ToJson());
    }

    [Fact]
    public async Task Expired_undiscoverable_route_does_not_allow_bootstrap_to_reset_an_existing_device_list()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.AddPeer(fixture.Account);
        var original = fixture.Relay.Devices[fixture.Account.AccountId].ToJson();
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new() { RelayId = fixture.Relay.RelayId }, Token));
        Assert.Equal(AccountEstablishmentReason.StateConflict, error.Reason);
        Assert.Equal(original, fixture.Relay.Devices[fixture.Account.AccountId].ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "account.route.publish");
        var request = Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        Assert.Equal(0, ProtocolModel.FromJson<AccountDeviceState>(request.Body!)!.Revision);
    }

    [Fact]
    public async Task Both_expired_route_and_certificate_renew_on_the_original_relay()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10), RouteValidity = TimeSpan.FromSeconds(10) }, Token);
        var id = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(id, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(1, fixture.Client.Route!.Revision);
        Assert.Equal(1, fixture.Client.DeviceState!.Revision);
        Assert.Equal(fixture.Relay.RelayId, fixture.Client.Route.RelayId);
    }

    [Fact]
    public async Task Different_pending_publication_returns_structured_conflict_and_resumes_after_reconciliation()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var other = fixture.Relay.AddPeer(fixture.Account).Certificate;
        var device = await fixture.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(7), Token);
        var pending = State(fixture, 1, [other]);
        await using (var db = fixture.Database.Open())
        {
            db.SignedRequests.Add(new()
            {
                Method = "device.state.publish",
                RelayId = fixture.Relay.RelayId,
                DocumentJson = pending.ToJson(),
                Revision = pending.Revision,
                Pending = true
            });
            await db.SaveChangesAsync(Token);
        }
        fixture.Relay.Requests.Clear();

        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

        Assert.Equal(AccountEstablishmentReason.OperationParametersConflict, error.Reason);
        Assert.IsAssignableFrom<InvalidOperationException>(error.InnerException);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        await using (var db = fixture.Database.Open())
        {
            var operationId = (await db.AccountEstablishments.SingleAsync(Token)).OperationId;
            Assert.NotEqual(Guid.Empty, operationId);
            Assert.Equal(device.GetDeviceId(TestNetwork.Context), (await db.AccountEstablishments.SingleAsync(Token)).PreviousLocalDeviceId);
            Assert.Equal(pending.ToJson(), (await db.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).DocumentJson);
        }

        // Once the original request is observed as authoritative, the next snapshot
        // may add the prepared device without replacing either its keys or the peer.
        fixture.Relay.Devices[fixture.Account.AccountId] = pending;
        await fixture.ReopenAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(device.GetDeviceId(TestNetwork.Context), fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(2, fixture.Client.DeviceState!.Revision);
        Assert.Equal(2, fixture.Client.DeviceState.Certificates.Length);
        Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.ToJson() == other.ToJson());
        Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
    }

    static AccountDeviceState State(TestClient fixture, long revision, IEnumerable<DeviceCertificate> certificates)
    {
        var state = new AccountDeviceState
        {
            Account = fixture.Account.AccountId,
            AccountPublicKey = fixture.Account.PublicKey,
            Revision = revision,
            Certificates = [.. certificates],
            AccountSignature = []
        };
        return state with { AccountSignature = [.. fixture.Account.Sign(state.GetSigningInput(TestNetwork.Context))] };
    }

    static async Task AddHistoryAsync(TestClient fixture, string deviceId)
    {
        await using var db = fixture.Database.Open();
        db.Messages.Add(new StoredMessageRecord
        {
            Sender = fixture.Account.AccountId,
            Recipient = fixture.Account.AccountId,
            MessageId = "history",
            SenderDeviceId = deviceId,
            PayloadType = "test",
            PayloadJson = "retained history",
            IsDirect = true
        });
        await db.SaveChangesAsync(Token);
    }

    sealed class CallbackSigner(AccountSigner inner) : IAccountSigner
    {
        public string AccountId => inner.AccountId;
        public Action? BeforeSign { get; set; }
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            BeforeSign?.Invoke();
            return inner.SignAsync(data, cancellationToken);
        }
    }
}
