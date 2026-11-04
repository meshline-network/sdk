using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using System.Security.Cryptography;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class ShutdownBoundaryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Renewal_observer_failure_after_receive_stops_is_preserved(bool cancellationError, bool overlapStop)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var pool = relay.Pool(account);
        relay.SessionSeconds = 30;
        using var release = new ManualResetEventSlim();
        Task? disposal = null;
        try
        {
            var client = await pool.GetAsync(relay.RelayId, new FailingRenewalSigner(new DeviceSigner(account, clock)), Token);
            var entered = AsyncTest.Signal();
            var notified = AsyncTest.Signal();
            Exception observerError = cancellationError ? new OperationCanceledException("renewal observer failed") : new IOException("renewal observer failed");
            var seen = 0;
            client.StateChanged += (_, _) =>
            {
                if (client.LastError is not CryptographicException { Message: "renewal signer unavailable" }
                    || Interlocked.CompareExchange(ref seen, 1, 0) != 0) return;
                if (overlapStop) disposal = client.DisposeAsync().AsTask();
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), Token));
                throw observerError;
            };
            client.ErrorOccurred += (_, error) => { if (ReferenceEquals(error, observerError)) notified.TrySetResult(); };
            client.StartNotifications();
            await AsyncTest.UntilAsync(() => relay.Sockets.Any(socket => socket.AuthenticationReplied.IsCompletedSuccessfully));
            await AsyncTest.UntilAsync(() => clock.HasTimerDueWithin(TimeSpan.FromSeconds(30)));
            clock.Advance(TimeSpan.FromSeconds(25));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (overlapStop) await AsyncTest.UntilAsync(() => relay.Sockets.Single().ClientStreamDisposed);
            release.Set();
            if (!overlapStop)
            {
                await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
                disposal = client.DisposeAsync().AsTask();
            }
            var error = await Record.ExceptionAsync(() => disposal!.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.NotNull(error);
            Assert.Same(observerError, Assert.Single(Flatten(error)));
            Assert.True(relay.Sockets.Single().ClientStreamDisposed);
            await client.DisposeAsync();
        }
        finally
        {
            release.Set();
            if (disposal is not null) await Record.ExceptionAsync(() => disposal.WaitAsync(TimeSpan.FromSeconds(5), Token));
            await Record.ExceptionAsync(() => pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Account_route_change_observer_failure_reaches_stop(bool cancellationError, bool laterRefresh)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.AccountManager;
        var previous = manager.Route!;
        var route = previous with { Revision = previous.Revision + 1 };
        route = route with { AccountSignature = [.. fixture.Account.Sign(route.GetAccountSigningInput(TestNetwork.Context))] };
        fixture.Relay.Routes[fixture.Account.AccountId] = fixture.Relay.SignRoute(route);
        var entered = AsyncTest.Signal();
        Exception observerError = cancellationError ? new OperationCanceledException("account change observer failed") : new IOException("account change observer failed");
        Task? stopping = null;
        var observed = 0;
        manager.AccountChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref observed, 1) != 0) return;
            if (!laterRefresh) stopping = manager.StopAsync(Token);
            entered.TrySetResult();
            throw observerError;
        };
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(previous, await manager.GetRouteAsync(cancellationToken: Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (laterRefresh)
        {
            // Join the first attempt if it is still delivering its notification,
            // then replace its result with a successful forced refresh.
            await Record.ExceptionAsync(() => manager.RefreshRouteAsync(cancellationToken: Token));
            Assert.NotNull(await manager.RefreshRouteAsync(cancellationToken: Token));
            stopping = manager.StopAsync(Token);
        }
        var error = await Record.ExceptionAsync(() => stopping!.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.NotNull(error);
        Assert.Same(observerError, Assert.Single(Flatten(error)));
        await manager.StopAsync(Token);
        await manager.StartAsync(Token);
        await manager.StopAsync(Token);
    }

    [Fact]
    public async Task Route_background_error_observer_is_not_reported_again_by_repeated_stop()
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        var fixture = new TestClient();
        try
        {
            await fixture.InitializeAsync();
            var manager = fixture.Client.AccountManager;
            fixture.Relay.Handler = (request, _) => request.Method == "account.route.resolve"
                ? throw new TimeoutException("route offline") : Task.FromResult(fixture.Relay.Respond(request));
            var observerError = new IOException("route error observer failed");
            manager.BackgroundError += (_, _) => throw observerError;
            var readError = await Record.ExceptionAsync(() => manager.RefreshRouteAsync(cancellationToken: Token));
            Assert.NotNull(readError);
            Assert.Contains(observerError, Flatten(readError));
            Assert.Same(observerError, await Record.ExceptionAsync(() => manager.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(5), Token)));
            fixture.Relay.Handler = null;
            Assert.Null(await Record.ExceptionAsync(() => manager.StopAsync(Token)));
            Assert.NotNull(await manager.RefreshRouteAsync(cancellationToken: Token));
            await manager.StartAsync(Token);
            await manager.StopAsync(Token);
        }
        finally { await Record.ExceptionAsync(() => fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_state_observer_and_retirement_failure_are_both_preserved(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        var fixture = new TestClient();
        try
        {
            await fixture.InitializeAsync();
            var relay = await fixture.Pool.GetAsync(fixture.Relay.RelayId, fixture.Client.DeviceManager, Token);
            Exception observerError = cancellationError ? new OperationCanceledException("device state observer failed") : new IOException("device state observer failed");
            var retirementError = new IOException("pool retirement observer failed");
            var retired = 0;
            fixture.Client.DeviceManager.DeviceStateChanged += (_, _) =>
            {
                if (fixture.Client.DeviceState!.Certificates.IsEmpty) throw observerError;
            };
            fixture.Pool.PoolChanged += (_, _) =>
            {
                if (!fixture.Pool.Clients.Contains(relay) && Interlocked.Exchange(ref retired, 1) == 0) throw retirementError;
            };
            var state = fixture.Relay.Devices[fixture.Account.AccountId] with
            {
                Revision = fixture.Relay.Devices[fixture.Account.AccountId].Revision + 1,
                Certificates = [],
                AccountSignature = []
            };
            fixture.Relay.Devices[fixture.Account.AccountId] = state with
            {
                AccountSignature = [.. fixture.Account.Sign(state.GetSigningInput(TestNetwork.Context))]
            };
            var error = await Record.ExceptionAsync(() => fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token));
            Assert.NotNull(error);
            Assert.Contains(retirementError, Flatten(error));
            Assert.Contains(observerError, Flatten(error));
            Assert.Equal(2, Flatten(error).Length);
            var stopError = await Record.ExceptionAsync(() => fixture.Client.DeviceManager.StopAsync(Token));
            Assert.NotNull(stopError);
            Assert.Equal(2, Flatten(stopError).Length);
            Assert.Contains(retirementError, Flatten(stopError));
            Assert.Contains(observerError, Flatten(stopError));
            await fixture.Client.DeviceManager.StopAsync(Token);
        }
        finally { await Record.ExceptionAsync(() => fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Socket_authentication_and_stop_observer_failures_are_both_preserved(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var pool = relay.Pool(account);
        try
        {
            var client = await pool.GetAsync(relay.RelayId, new DeviceSigner(account, relay.Clock), Token);
            Exception firstError = cancellationError ? new OperationCanceledException("connected observer failed") : new IOException("connected observer failed");
            var cleanupError = new IOException("disconnected observer failed");
            var connected = 0;
            var disconnected = 0;
            var notified = AsyncTest.Signal();
            client.StateChanged += (_, _) =>
            {
                if (client.ConnectionState == RelayConnectionState.Connected && Interlocked.Exchange(ref connected, 1) == 0) throw firstError;
                if (client.ConnectionState == RelayConnectionState.Disconnected && Interlocked.Exchange(ref disconnected, 1) == 0) throw cleanupError;
            };
            client.ErrorOccurred += (_, _) => notified.TrySetResult();
            client.StartNotifications();
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var error = await Record.ExceptionAsync(() => pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.NotNull(error);
            var failures = Flatten(error);
            Assert.Equal(2, failures.Length);
            Assert.Contains(firstError, failures);
            Assert.Contains(cleanupError, failures);
            Assert.All(relay.Sockets, socket => Assert.True(socket.ClientStreamDisposed));
        }
        finally { await Record.ExceptionAsync(() => pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_disposal_drains_conversation_and_all_children_after_observer_failures(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        new ChannelRelay(fixture).Install();
        var channel = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
        Exception observerError = cancellationError ? new OperationCanceledException("conversation observer failed") : new IOException("conversation observer failed");
        var childError = new IOException("child disposal observer failed");
        var entered = AsyncTest.Signal();
        Task? disposal = null;
        fixture.Client.ConversationChanged += (_, _) =>
        {
            disposal = fixture.Client.DisposeAsync().AsTask();
            entered.TrySetResult();
            throw observerError;
        };
        fixture.Client.GroupManager.StateChanged += (_, _) =>
        {
            if (fixture.Client.GroupManager.LifecycleState == ComponentState.Disposed) throw childError;
        };
        await fixture.Client.ChannelManager.FollowAsync(channel.Ref, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var error = await Record.ExceptionAsync(() => disposal!.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(error);
        var failures = Flatten(error);
        Assert.Equal(2, failures.Length);
        Assert.Contains(observerError, failures);
        Assert.Contains(childError, failures);
        Assert.All(new ClientComponent[] { fixture.Client.AccountManager, fixture.Client.DeviceManager, fixture.Client.ProfileManager,
            fixture.Client.MessageManager, fixture.Client.ChannelManager, fixture.Client.GroupManager }, child => Assert.Equal(ComponentState.Disposed, child.LifecycleState));
        await fixture.Client.DisposeAsync();
        Assert.Equal(ComponentState.Disposed, fixture.Client.LifecycleState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorization_stop_retains_error_observer_failure_until_disposal(bool cancellationError)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.StartAsync(Token);
        await fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
        Exception stopError = cancellationError ? new OperationCanceledException("child stop observer failed") : new IOException("child stop observer failed");
        var reportError = new IOException("automatic stop error observer failed");
        var reported = AsyncTest.Signal();
        fixture.Client.GroupManager.StateChanged += (_, _) =>
        {
            if (fixture.Client.GroupManager.LifecycleState == ComponentState.Stopped) throw stopError;
        };
        fixture.Client.BackgroundError += (_, args) =>
        {
            if (!ReferenceEquals(args.Error, stopError)) return;
            reported.TrySetResult();
            throw reportError;
        };
        var state = fixture.Client.DeviceState! with { Revision = fixture.Client.DeviceState!.Revision + 1, Certificates = [], AccountSignature = [] };
        fixture.Relay.Devices[fixture.Account.AccountId] = state with { AccountSignature = [.. fixture.Account.Sign(state.GetSigningInput(TestNetwork.Context))] };
        var readError = await Record.ExceptionAsync(() => fixture.Client.DeviceManager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token));
        Assert.True(readError is null or OperationCanceledException or ObjectDisposedException, readError?.ToString());
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var error = await Record.ExceptionAsync(() => fixture.Client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.NotNull(error);
        var failures = Flatten(error);
        Assert.Equal(2, failures.Length);
        Assert.Contains(stopError, failures);
        Assert.Contains(reportError, failures);
        await fixture.Client.DisposeAsync();
    }

    static Exception[] Flatten(Exception error) => error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [error];

    sealed class FailingRenewalSigner(IDeviceSigner inner) : IDeviceSigner
    {
        int calls;
        public DeviceCertificate Certificate => inner.Certificate;
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref calls) == 3 ? throw new CryptographicException("renewal signer unavailable") : inner.SignAsync(data, cancellationToken);
    }
}
