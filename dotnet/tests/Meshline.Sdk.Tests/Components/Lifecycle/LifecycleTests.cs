using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class LifecycleTests
{
    sealed class Component(ClientOptions options) : ClientComponent(options)
    {
        public int Starts, Stops, Initializes;
        public bool FailStart;
        public TaskCompletionSource Entered = AsyncTest.Signal();
        protected override Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            Initializes++;
            return Task.CompletedTask;
        }

        protected override Task OnStartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            if (FailStart)
                throw new IOException("start failed");
            return Task.CompletedTask;
        }

        protected override Task OnStopAsync()
        {
            Stops++;
            return Task.CompletedTask;
        }

        public async Task OperationAsync()
        {
            var token = CancellationToken.None;
            using var operation = BeginOperation(ref token);
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        public void Error(Exception error) => ReportBackgroundError(BackgroundOperation.Connect, "relay", error);
    }

    [Fact]
    public async Task Concurrent_lifecycle_calls_are_idempotent_and_disposal_is_terminal()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var signer = new AccountSigner();
        await using var component = new Component(relay.Options(signer));

        await Assert.ThrowsAsync<InvalidOperationException>(() => component.StartAsync(cancellationToken: TestContext.Current.CancellationToken));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => component.InitializeAsync()));

        Assert.Equal(1, component.Initializes);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => component.StartAsync()));

        Assert.Equal(1, component.Starts);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => component.StopAsync()));

        Assert.Equal(1, component.Stops);

        await component.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await component.DisposeAsync();
        await component.DisposeAsync();

        Assert.Equal(ComponentState.Disposed, component.LifecycleState);
        Assert.Equal(2, component.Stops);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => component.StartAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_start_is_cleaned_up_and_can_be_retried()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var signer = new AccountSigner();
        await using var component = new Component(relay.Options(signer));
        await component.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        component.FailStart = true;

        await Assert.ThrowsAsync<IOException>(() => component.StartAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ComponentState.Stopped, component.LifecycleState);
        Assert.Equal(1, component.Stops);

        component.FailStart = false;
        await component.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Running, component.LifecycleState);
    }

    [Fact]
    public async Task Disposal_cancels_and_drains_active_operations()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var signer = new AccountSigner();
        await using var component = new Component(relay.Options(signer));
        var operation = component.OperationAsync();
        await component.Entered.Task;
        await component.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Background_errors_retain_the_original_exception()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var signer = new AccountSigner();
        await using var component = new Component(relay.Options(signer));
        BackgroundErrorEventArgs? observed = null;
        component.BackgroundError += (_, error) => observed = error;
        var expected = new IOException("diagnostic");
        component.Error(expected);

        Assert.NotNull(observed);
        Assert.Same(expected, observed.Error);
    }
}
