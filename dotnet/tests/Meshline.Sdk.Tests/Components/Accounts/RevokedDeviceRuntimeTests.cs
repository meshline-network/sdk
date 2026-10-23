using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class RevokedDeviceRuntimeTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_revocation_stops_all_children_and_explicit_reauthorization_can_restart(bool backgroundRead)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.StartAsync(Token);
        var device = fixture.Client.Device!.ToJson();
        var stopped = AsyncTest.Signal();
        fixture.Client.StateChanged += (_, change) =>
        {
            if (fixture.Client.LifecycleState == ComponentState.Stopped) stopped.TrySetResult();
        };
        fixture.Relay.Requests.Clear();
        Revoke(fixture);
        if (backgroundRead)
        {
            fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(61));
            // The cached read can return before its owned refresh notices removal.
            await fixture.Client.DeviceManager.GetDeviceStateAsync(cancellationToken: Token);
        }
        else await fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.All(new ClientComponent[] { fixture.Client.AccountManager, fixture.Client.DeviceManager, fixture.Client.ProfileManager,
            fixture.Client.MessageManager, fixture.Client.ChannelManager, fixture.Client.GroupManager }, child => Assert.Equal(ComponentState.Stopped, child.LifecycleState));
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method is "device.state.publish" or "account.route.publish");
        await using (var db = fixture.Database.Open()) Assert.Single(await db.LocalDevices.ToListAsync(Token));
        // Local history is available without restarting or authorizing the runtime.
        await using (var history = await fixture.Client.MessageManager.GetMessageHistoryAsync(cancellationToken: Token)) { }
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        await fixture.Client.StartAsync(Token);
        Assert.Equal(ComponentState.Running, fixture.Client.LifecycleState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_queries_and_expired_certificates_are_not_explicit_revocation(bool expires)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromMinutes(5) }, Token);
        await fixture.Client.StartAsync(Token);
        if (expires)
        {
            fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(6));
            var state = fixture.Client.DeviceState! with { Revision = fixture.Client.DeviceState!.Revision + 1, AccountSignature = [] };
            fixture.Relay.Devices[fixture.Account.AccountId] = state with { AccountSignature = [.. fixture.Account.Sign(state.GetSigningInput(TestNetwork.Context))] };
            await fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
        }
        else
        {
            fixture.Relay.Handler = (request, _) => request.Method == "device.state.resolve"
                ? throw new HttpRequestException("offline") : Task.FromResult(fixture.Relay.Respond(request));
            await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token));
        }
        Assert.Equal(ComponentState.Running, fixture.Client.LifecycleState);
        Assert.Single(fixture.Client.DeviceState!.Certificates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_stop_cooperates_with_explicit_stop_or_disposal(bool dispose)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.StartAsync(Token);
        Task? shutdown = null;
        fixture.Client.DeviceManager.DeviceStateChanged += (_, _) =>
        {
            if (fixture.Client.DeviceState!.Certificates.IsEmpty)
                shutdown = dispose ? fixture.Client.DisposeAsync().AsTask() : fixture.Client.StopAsync(Token);
        };
        Revoke(fixture);
        var read = fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
        var error = await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.True(error is null or OperationCanceledException or ObjectDisposedException, error?.ToString());
        Assert.NotNull(shutdown);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(dispose ? ComponentState.Disposed : ComponentState.Stopped, fixture.Client.LifecycleState);
        if (!dispose)
        {
            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
            await fixture.Client.StartAsync(Token);
            Assert.Equal(ComponentState.Running, fixture.Client.LifecycleState);
        }
    }

    [Fact]
    public async Task Revocation_during_startup_cannot_leave_a_running_client()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        Task? observation = null;
        fixture.Client.DeviceManager.StateChanged += (_, _) =>
        {
            if (fixture.Client.DeviceManager.LifecycleState == ComponentState.Running)
            {
                Revoke(fixture);
                observation = fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
            }
        };
        var failure = await Record.ExceptionAsync(() => fixture.Client.StartAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(observation);
        await observation.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AsyncTest.UntilAsync(() => fixture.Client.LifecycleState == ComponentState.Stopped);
        Assert.False(failure is TimeoutException, failure?.ToString());
        Assert.Equal(ComponentState.Stopped, fixture.Client.MessageManager.LifecycleState);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rejected_device_access_stops_runtime_without_account_signing_or_inventing_revocation(bool signerAvailable, bool backgroundRead)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var device = fixture.Client.Device!.ToJson();
        var state = fixture.Client.DeviceState!.ToJson();
        await fixture.Client.DisposeAsync();
        await using var pool = fixture.Relay.Pool(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            pool, fixture.Protector, signerAvailable ? fixture.Account : null);
        await client.InitializeAsync(Token);
        await client.StartAsync(Token);
        var stopped = AsyncTest.Signal();
        client.StateChanged += (_, _) =>
        {
            if (client.LifecycleState == ComponentState.Stopped) stopped.TrySetResult();
        };
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method switch
        {
            "device.state.resolve" => OfflineRelay.Error("unauthorized"),
            "auth.device.verify" => OfflineRelay.Error("device_unknown"),
            _ => fixture.Relay.Respond(request)
        });
        fixture.Relay.Requests.Clear();
        if (backgroundRead)
        {
            fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(61));
            await client.DeviceManager.GetDeviceStateAsync(cancellationToken: Token);
        }
        else
        {
            var error = await Record.ExceptionAsync(() => client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token));
            Assert.True(error is RelayException or OperationCanceledException, error?.ToString());
        }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.All(new ClientComponent[] { client.AccountManager, client.DeviceManager, client.ProfileManager,
            client.MessageManager, client.ChannelManager, client.GroupManager }, child => Assert.Equal(ComponentState.Stopped, child.LifecycleState));
        Assert.Equal(device, client.Device!.ToJson());
        Assert.Equal(state, client.DeviceState!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "auth.account.verify" or "device.state.publish" or "account.route.publish");
        await using (var history = await client.MessageManager.GetMessageHistoryAsync(cancellationToken: Token)) { }
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        await client.StartAsync(Token);
        Assert.Equal(ComponentState.Running, client.LifecycleState);
        var home = await pool.GetAsync(fixture.Relay.RelayId, client.DeviceManager, Token);
        Assert.NotNull(await home.SendHttpAsync<AccountDeviceState>(HttpMethod.Get, "device.state.resolve",
            new AccountQuery { Account = fixture.Account.AccountId }, cancellationToken: Token));
        Assert.Contains(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "auth.account.verify");
    }

    [Fact]
    public async Task Explicit_setup_can_use_account_authorization_after_device_authentication_is_rejected()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var device = fixture.Client.Device!.ToJson();
        Revoke(fixture);
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(6));
        var accountSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        fixture.Relay.Handler = async (request, token) =>
        {
            var removed = fixture.Relay.Devices[fixture.Account.AccountId].Certificates.IsEmpty;
            if (removed && request.Method == "auth.device.verify") return OfflineRelay.Error("device_unknown");
            if (removed && request.Method == "device.state.resolve" && !accountSessions.ContainsKey(request.Session ?? ""))
                return OfflineRelay.Error("unauthorized");
            var response = fixture.Relay.Respond(request);
            if (request.Method == "auth.account.verify")
            {
                var credentials = ProtocolModel.FromJson<SessionCredentials>(await response.Content.ReadAsStringAsync(token))!;
                accountSessions[credentials.Token] = true;
            }
            return response;
        };

        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.Equal(device, Assert.Single(fixture.Client.DeviceState!.Certificates).ToJson());
        Assert.NotEmpty(accountSessions);
        await fixture.Client.StartAsync(Token);
        Assert.Equal(ComponentState.Running, fixture.Client.LifecycleState);
    }

    [Fact]
    public async Task Expired_session_reauthenticates_same_device_without_stopping_or_signing_account()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.StartAsync(Token);
        var reads = 0;
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.resolve" && Interlocked.Increment(ref reads) == 1
            ? OfflineRelay.Error("unauthorized") : fixture.Relay.Respond(request));
        fixture.Relay.Requests.Clear();

        Assert.NotNull(await fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token));

        Assert.Equal(2, reads);
        Assert.Equal(ComponentState.Running, fixture.Client.LifecycleState);
        Assert.Single(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "auth.account.verify");
    }

    [Fact]
    public async Task Http_only_authentication_rejection_stops_runtime_even_without_device_state_refresh()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        fixture.Relay.SessionSeconds = 5;
        fixture.Relay.Descriptor = fixture.Relay.SignDescriptor(fixture.Relay.Descriptor with
        {
            Endpoints = [.. fixture.Relay.Descriptor.Endpoints.Where(value => !value.StartsWith("wss://", StringComparison.Ordinal))]
        });
        await fixture.InitializeAsync();
        await fixture.Client.StartAsync(Token);
        var home = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        var stopped = AsyncTest.Signal();
        fixture.Client.StateChanged += (_, _) =>
        {
            if (fixture.Client.LifecycleState == ComponentState.Stopped) stopped.TrySetResult();
        };
        var state = fixture.Client.DeviceState!.ToJson();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "auth.device.verify"
            ? OfflineRelay.Error("device_unknown") : fixture.Relay.Respond(request));
        fixture.Relay.Requests.Clear();
        // Expire the session without also expiring a startup request's 60-second deadline.
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(6));

        var error = await Record.ExceptionAsync(() => home.SendHttpAsync<AccountDeviceState>(HttpMethod.Get, "device.state.resolve",
            new AccountQuery { Account = fixture.Account.AccountId }, cancellationToken: Token));
        Assert.True(error is RelayException or OperationCanceledException, error?.ToString());

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Contains(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
        Assert.Equal(state, fixture.Client.DeviceState!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "auth.account.verify" or "device.state.resolve");
    }

    [Fact]
    public async Task Peer_query_and_recipient_authorization_errors_do_not_stop_local_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        await fixture.Client.StartAsync(Token);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.resolve"
            ? OfflineRelay.Error("unauthorized") : request.Method == "message.send"
                ? OfflineRelay.Error("device_unknown") : fixture.Relay.Respond(request));

        await Assert.ThrowsAsync<RelayException>(() => fixture.Client.DeviceManager.RefreshDeviceStateAsync(peer.AccountId, Token));
        var home = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
        await Assert.ThrowsAsync<RelayException>(() => home.SendHttpAsync<AccountDeviceState>(HttpMethod.Post, "message.send",
            new AccountQuery { Account = peer.AccountId }, cancellationToken: Token));

        Assert.Equal(ComponentState.Running, fixture.Client.LifecycleState);
        fixture.Relay.Handler = null;
        await fixture.Client.StopAsync(Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_suspension_drains_all_device_sockets_and_restart_authenticates_once(bool rejectAuthentication)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var remote = new OfflineRelay("other.test");
        fixture.Relay.LinkTo(remote);
        await fixture.InitializeAsync();
        await fixture.Client.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Count != 0);
        var socket = fixture.Relay.Sockets.Last();
        await socket.AuthenticationReplied.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var other = await fixture.Pool.GetAsync(remote.RelayId, fixture.Client.DeviceManager, Token);
        other.StartNotifications();
        await AsyncTest.UntilAsync(() => remote.Sockets.Count != 0);
        var otherSocket = remote.Sockets.Last();
        await otherSocket.AuthenticationReplied.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var accountSession = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Account, Token);
        var retained = fixture.Client.DeviceState!.ToJson();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method switch
        {
            "device.state.resolve" => OfflineRelay.Error("unauthorized"),
            "auth.device.verify" when rejectAuthentication => OfflineRelay.Error("device_unknown"),
            _ => fixture.Relay.Respond(request)
        });

        await Record.ExceptionAsync(() => fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token));
        await AsyncTest.UntilAsync(() => fixture.Client.LifecycleState == ComponentState.Stopped);

        Assert.True(socket.ClientStreamDisposed);
        Assert.True(otherSocket.ClientStreamDisposed);
        Assert.DoesNotContain(fixture.Pool.Clients, client => client.SessionMode == SessionMode.Device);
        Assert.Contains(accountSession, fixture.Pool.Clients);
        Assert.Equal(retained, fixture.Client.DeviceState!.ToJson());
        var httpRequests = fixture.Relay.Requests.Count + remote.Requests.Count;
        var socketRequests = socket.Requests.Count + otherSocket.Requests.Count;
        var upgrades = fixture.Relay.Upgrades.Count + remote.Upgrades.Count;
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50, Token);
        Assert.Equal(httpRequests, fixture.Relay.Requests.Count + remote.Requests.Count);
        Assert.Equal(socketRequests, socket.Requests.Count + otherSocket.Requests.Count);
        Assert.Equal(upgrades, fixture.Relay.Upgrades.Count + remote.Upgrades.Count);

        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        await fixture.Client.StartAsync(Token);
        Assert.Equal(ComponentState.Running, fixture.Client.LifecycleState);
        Assert.Single(fixture.Relay.Requests, request => request.Method == "auth.device.verify");
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "auth.account.verify" or "device.state.publish" or "account.route.publish");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outbox_cleanup_failure_still_stops_all_children_and_preserves_retry(bool authenticationStop)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var device = fixture.Client.Device!.ToJson();
        var sending = AsyncTest.Signal();
        var stopped = AsyncTest.Signal();
        var cleanupFailed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectAuthorization = false;
        string? requestJson = null;
        fixture.Client.StateChanged += (_, change) =>
        {
            if (change.CurrentState == ComponentState.Stopped) stopped.TrySetResult();
        };
        fixture.Client.BackgroundError += (_, args) =>
        {
            if (args.Error is DbUpdateException) cleanupFailed.TrySetResult(args.Error);
        };
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "message.send")
            {
                requestJson = request.Body;
                sending.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return request.Method == "device.state.resolve" && rejectAuthorization
                ? OfflineRelay.Error("unauthorized") : fixture.Relay.Respond(request);
        };
        await fixture.Client.StartAsync(Token);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Count != 0);
        var socket = fixture.Relay.Sockets.Last();
        await socket.AuthenticationReplied.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_outbox_cleanup BEFORE UPDATE ON MessageOutbox WHEN NEW.State = 4 BEGIN SELECT RAISE(FAIL, 'injected outbox write failure'); END;", Token);

        try
        {
            Exception error;
            if (authenticationStop)
            {
                rejectAuthorization = true;
                await Record.ExceptionAsync(() => fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token));
                error = await cleanupFailed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
            else
                error = await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.StopAsync(Token));
            Assert.Contains("injected outbox write failure", error.InnerException!.Message);
            Assert.All(new ClientComponent[] { fixture.Client, fixture.Client.AccountManager, fixture.Client.DeviceManager,
                fixture.Client.ProfileManager, fixture.Client.MessageManager, fixture.Client.ChannelManager, fixture.Client.GroupManager },
                component => Assert.Equal(ComponentState.Stopped, component.LifecycleState));
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(socket.ClientStreamDisposed);
            Assert.DoesNotContain(fixture.Pool.Clients, client => client.SessionMode == SessionMode.Device);
            Assert.Equal(device, fixture.Client.Device!.ToJson());
            var requests = fixture.Relay.Requests.Count;
            var upgrades = fixture.Relay.Upgrades.Count;
            fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(10));
            await Task.Delay(50, Token);
            Assert.Equal(requests, fixture.Relay.Requests.Count);
            Assert.Equal(upgrades, fixture.Relay.Upgrades.Count);
        }
        finally
        {
            fixture.Relay.Handler = null;
            await using var database = fixture.Database.Open();
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_outbox_cleanup;", Token);
        }

        // A completed fault is reported once, and no longer blocks cleanup or setup.
        await fixture.Client.StopAsync(Token);
        Revoke(fixture);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        await using (var database = fixture.Database.Open())
        {
            var pending = await database.MessageOutbox.SingleAsync(value => value.RequestJson == requestJson, Token);
            Assert.Equal(MessageSendState.Submitting, pending.State);
        }

        // Reopening after the storage failure reconciles the unchanged interrupted send.
        await fixture.ReopenAsync();
        await using (var database = fixture.Database.Open())
            Assert.Equal(MessageSendState.SubmissionUnknown,
                (await database.MessageOutbox.SingleAsync(value => value.RequestJson == requestJson, Token)).State);
        fixture.Relay.Requests.Clear();
        await fixture.Client.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Requests.Any(request => request.Method == "message.send" && request.Body == requestJson));
        await fixture.Client.StopAsync(Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_cancellation_errors_still_drain_device_sessions_and_preserve_all_failures(bool failAccountToo)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Count != 0);
        var socket = fixture.Relay.Sockets.Last();
        await socket.AuthenticationReplied.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var deviceEntered = AsyncTest.Signal();
        var accountEntered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var deviceFailure = new IOException("device query cancellation failed");
        var accountFailure = new IOException("account query cancellation failed");
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve" || failAccountToo && request.Method == "account.route.resolve")
            {
                var deviceQuery = request.Method == "device.state.resolve";
                using var registration = token.Register(() => throw (deviceQuery ? deviceFailure : accountFailure));
                (deviceQuery ? deviceEntered : accountEntered).TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        var deviceRead = fixture.Client.DeviceManager.RefreshDeviceStateAsync(cancellationToken: Token);
        var accountRead = failAccountToo ? fixture.Client.AccountManager.RefreshRouteAsync(cancellationToken: Token) : null;
        Task? stopping = null;
        try
        {
            await deviceEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (failAccountToo) await accountEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            stopping = fixture.Client.StopAsync(Token);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(ComponentState.Stopping, fixture.Client.LifecycleState);
        }
        finally { release.TrySetResult(); }

        var failure = await Assert.ThrowsAsync<AggregateException>(() => stopping!.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Contains(deviceFailure, failure.Flatten().InnerExceptions);
        if (failAccountToo) Assert.Contains(accountFailure, failure.Flatten().InnerExceptions);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deviceRead);
        if (accountRead is not null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accountRead);
        Assert.True(socket.ClientStreamDisposed);
        Assert.DoesNotContain(fixture.Pool.Clients, client => client.SessionMode == SessionMode.Device);
        Assert.All(new ClientComponent[] { fixture.Client, fixture.Client.AccountManager, fixture.Client.DeviceManager,
            fixture.Client.ProfileManager, fixture.Client.MessageManager, fixture.Client.ChannelManager, fixture.Client.GroupManager },
            component => Assert.Equal(ComponentState.Stopped, component.LifecycleState));

        fixture.Relay.Handler = null;
        await fixture.Client.StopAsync(Token);
        await fixture.Client.StartAsync(Token);
        Assert.Equal(ComponentState.Running, fixture.Client.LifecycleState);
    }

    static void Revoke(TestClient fixture)
    {
        var state = fixture.Client.DeviceState! with { Revision = fixture.Client.DeviceState!.Revision + 1, Certificates = [], AccountSignature = [] };
        fixture.Relay.Devices[fixture.Account.AccountId] = state with { AccountSignature = [.. fixture.Account.Sign(state.GetSigningInput(TestNetwork.Context))] };
    }
}
