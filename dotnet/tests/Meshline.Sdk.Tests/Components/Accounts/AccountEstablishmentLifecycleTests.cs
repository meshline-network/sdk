using Meshline.Components;
using Meshline.Interactions;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountEstablishmentLifecycleTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disposal_cancels_setup_while_start_waits_and_retains_the_pending_profile_request()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish")
            {
                entered.TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        using var fallback = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var setup = fixture.Client.EstablishAccountAsync(cancellationToken: fallback.Token);
        Task? start = null;
        Task? dispose = null;
        string device;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            device = fixture.Client.Device!.ToJson();
            start = fixture.Client.StartAsync(fallback.Token);
            Assert.False(start.IsCompleted);
            dispose = fixture.Client.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(dispose.IsCompleted);
            Assert.False(setup.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            fallback.Cancel();
            if (start is not null) await Record.ExceptionAsync(() => start.WaitAsync(TimeSpan.FromSeconds(5), Token));
            await Record.ExceptionAsync(() => setup.WaitAsync(TimeSpan.FromSeconds(5), Token));
            if (dispose is not null) await dispose.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup);
        await using (var db = fixture.Database.Open())
        {
            Assert.Single(await db.AccountEstablishments.ToListAsync(Token));
            Assert.True((await db.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);
        }
        var pending = Assert.Single(fixture.Relay.Requests, request => request.Method == "profile.publish").Body;
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.Equal(pending, Assert.Single(fixture.Relay.Requests, request => request.Method == "profile.publish").Body);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "device.state.publish" or "account.route.publish");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_waits_for_executing_establishment_and_can_cancel_its_wait(bool cancelStart)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Client.DisposeAsync();
        var signer = new PausingStateSigner(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        var establishment = client.EstablishAccountAsync(cancellationToken: Token);
        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task? starting = null;
        try
        {
            await signer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            starting = client.StartAsync(startCancellation.Token);
            Assert.False(starting.IsCompleted);
            Assert.Equal(ComponentState.Stopped, client.AccountManager.LifecycleState);
            Assert.Equal(ComponentState.Stopped, client.DeviceManager.LifecycleState);
            if (cancelStart)
            {
                startCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(TimeSpan.FromSeconds(5), Token));
            }
        }
        finally
        {
            signer.Release.TrySetResult();
            await establishment.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (starting is not null) await Record.ExceptionAsync(() => starting.WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
        if (cancelStart) await client.StartAsync(Token);
        else await starting!;
        Assert.Equal(ComponentState.Running, client.LifecycleState);
        await client.StopAsync(Token);
    }

    [Fact]
    public async Task Canceling_start_while_waiting_does_not_cancel_establishments_state_query()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var queryCanceled = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                using var registration = token.Register(() => queryCanceled.TrySetResult());
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var establishment = fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var starting = fixture.Client.StartAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.False(queryCanceled.Task.IsCompleted);
            Assert.False(establishment.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await establishment.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
    }

    [Fact]
    public async Task Startup_reserves_authorization_before_the_facade_reports_running()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Client.AccountManager.StateChanged += (_, _) =>
        {
            if (fixture.Client.AccountManager.LifecycleState != ComponentState.Running) return;
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Startup checkpoint.");
        };
        var starting = Task.Run(() => fixture.Client.StartAsync(Token), Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(ComponentState.Stopped, fixture.Client.LifecycleState);
            fixture.Relay.Requests.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.EstablishAccountAsync(new()
            {
                ReplaceDeviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context)
            }, Token));
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "device.state.publish" or "account.route.publish");
            await using var db = fixture.Database.Open();
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
        }
        finally
        {
            release.TrySetResult();
            await starting.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await fixture.Client.StopAsync(Token);
        }
        // Stopping clears the reservation: a later explicit reauthorization works.
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [], cancellationToken: Token);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_canceled_start_releases_authorization_after_cleanup(bool cancelStart)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        fixture.Client.AccountManager.StateChanged += (_, _) =>
        {
            if (cancelStart && fixture.Client.AccountManager.LifecycleState == ComponentState.Running)
                cancellation.Cancel();
        };
        if (cancelStart)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.StartAsync(cancellation.Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.StartAsync(Token));
        Assert.Equal(ComponentState.Stopped, fixture.Client.AccountManager.LifecycleState);
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        await fixture.Client.StartAsync(Token);
        await fixture.Client.StopAsync(Token);
    }

    sealed class PausingStateSigner(AccountSigner inner) : IAccountSigner
    {
        public string AccountId => inner.AccountId;
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();
        public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetString(data.Span).Contains("meshline.account.device.state", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.SignAsync(data, cancellationToken);
        }
    }
}
