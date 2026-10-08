using Meshline.Components;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class LifecycleTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class PendingWork
    {
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Canceled { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();
        public bool Completed;

        public async Task RunAsync(CancellationToken token, Exception? cancellationError = null)
        {
            Completed = false;
            using var callback = token.Register(() =>
            {
                if (cancellationError is not null)
                    throw cancellationError;
            });
            Entered.TrySetResult();
            try { await AsyncTest.WaitForCancellationAndCleanupAsync(token, Canceled, Release.Task); }
            finally { Completed = true; }
        }
    }

    sealed class Component(AccountSigner account) : ClientComponent(new() { Context = TestNetwork.Context, AccountId = account.AccountId })
    {
        Task background = Task.CompletedTask;
        public PendingWork Background { get; } = new();
        public PendingWork Foreground { get; } = new();
        public TaskCompletionSource StopEntered { get; } = AsyncTest.Signal();
        public Exception? RuntimeError, OperationError, StartError, StopError, DisposalError;
        public int Starts, Stops, Initializes, Disposals;
        public bool CleanupBeforeDrain;
        public CancellationToken RuntimeToken => RuntimeCancellationToken;

        protected override Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            Initializes++;
            return Task.CompletedTask;
        }

        protected override Task OnStartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            background = Background.RunAsync(RuntimeCancellationToken, RuntimeError);
            if (StartError is not null)
                throw StartError;
            return Task.CompletedTask;
        }

        protected override async Task OnStopAsync()
        {
            var error = StopError;
            Stops++;
            StopEntered.TrySetResult();
            try { await background; }
            catch (OperationCanceledException) when (RuntimeCancellationToken.IsCancellationRequested) { }
            if (error is not null)
                throw error;
        }

        protected override ValueTask DisposeAsyncCore()
        {
            Disposals++;
            CleanupBeforeDrain |= Foreground.Entered.Task.IsCompleted && !Foreground.Completed
                || Background.Entered.Task.IsCompleted && !Background.Completed;
            if (DisposalError is not null)
                throw DisposalError;
            return ValueTask.CompletedTask;
        }

        public async Task OperationAsync()
        {
            var token = CancellationToken.None;
            using var operation = BeginOperation(ref token);
            await Foreground.RunAsync(token, OperationError);
        }

        public void Error(Exception error) => ReportBackgroundError(BackgroundOperation.Connect, "relay", error);

        public void ReleaseForCleanup()
        {
            RuntimeError = OperationError = StartError = StopError = DisposalError = null;
            Background.Release.TrySetResult();
            Foreground.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Concurrent_lifecycle_calls_are_idempotent_and_disposal_is_terminal()
    {
        using var signer = new AccountSigner();
        await using var component = new Component(signer);
        component.ReleaseForCleanup();

        await Assert.ThrowsAsync<InvalidOperationException>(() => component.StartAsync(cancellationToken: Token));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => component.InitializeAsync()));

        Assert.Equal(1, component.Initializes);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => component.StartAsync()));

        Assert.Equal(1, component.Starts);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => component.StopAsync()));

        Assert.Equal(1, component.Stops);

        await component.StartAsync(cancellationToken: Token);
        await component.DisposeAsync();
        await component.DisposeAsync();

        Assert.Equal(ComponentState.Disposed, component.LifecycleState);
        Assert.Equal(2, component.Stops);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => component.StartAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task Failed_start_is_cleaned_up_and_can_be_retried()
    {
        using var signer = new AccountSigner();
        await using var component = new Component(signer);
        component.ReleaseForCleanup();
        await component.InitializeAsync(cancellationToken: Token);
        component.StartError = new IOException("start failed");

        await Assert.ThrowsAsync<IOException>(() => component.StartAsync(cancellationToken: Token));
        Assert.Equal(ComponentState.Stopped, component.LifecycleState);
        Assert.Equal(1, component.Stops);

        component.StartError = null;
        await component.StartAsync(cancellationToken: Token);

        Assert.Equal(ComponentState.Running, component.LifecycleState);
    }

    [Fact]
    public async Task Disposal_cancels_and_drains_active_operations()
    {
        using var signer = new AccountSigner();
        await using var component = new Component(signer);
        component.ReleaseForCleanup();
        var operation = component.OperationAsync();
        await component.Foreground.Entered.Task;
        await component.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Background_errors_retain_the_original_exception()
    {
        using var signer = new AccountSigner();
        await using var component = new Component(signer);
        component.ReleaseForCleanup();
        BackgroundErrorEventArgs? observed = null;
        component.BackgroundError += (_, error) => observed = error;
        var expected = new IOException("diagnostic");
        component.Error(expected);

        Assert.NotNull(observed);
        Assert.Same(expected, observed.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_drains_background_and_preserves_callback_and_hook_errors(bool failHook)
    {
        using var account = new AccountSigner();
        await using var component = new Component(account);
        var callbackError = new InvalidOperationException("runtime callback failed");
        var hookError = new IOException("stop hook failed");
        component.RuntimeError = callbackError;
        component.StopError = failHook ? hookError : null;
        await component.InitializeAsync(Token);
        await component.StartAsync(Token);
        var previousToken = component.RuntimeToken;
        var stopped = component.StopAsync(Token);
        try
        {
            Assert.Same(component.StopEntered.Task, await Task.WhenAny(component.StopEntered.Task, stopped).WaitAsync(TimeSpan.FromSeconds(10), Token));
            await component.Background.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(stopped.IsCompleted);
            Assert.Equal(ComponentState.Stopping, component.LifecycleState);
        }
        finally { component.ReleaseForCleanup(); }

        var error = await Assert.ThrowsAsync<AggregateException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Contains(callbackError, error.Flatten().InnerExceptions);
        if (failHook)
            Assert.Contains(hookError, error.Flatten().InnerExceptions);
        component.StopError = null;
        Assert.True(component.Background.Completed);
        Assert.Equal(ComponentState.Stopped, component.LifecycleState);
        Assert.Throws<InvalidOperationException>(() => component.RuntimeToken);

        await component.StartAsync(Token);
        Assert.NotEqual(previousToken, component.RuntimeToken);
        Assert.False(component.RuntimeToken.IsCancellationRequested);
        await component.StopAsync(Token);
        Assert.Equal(2, component.Stops);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Disposal_drains_operations_and_background_before_reporting_callback_errors(bool start, bool failOperation, bool failRuntime)
    {
        using var account = new AccountSigner();
        await using var component = new Component(account);
        var operationError = new InvalidOperationException("operation callback failed");
        var runtimeError = new InvalidOperationException("runtime callback failed");
        component.OperationError = failOperation ? operationError : null;
        component.RuntimeError = failRuntime ? runtimeError : null;
        await component.InitializeAsync(Token);
        if (start)
            await component.StartAsync(Token);
        var operation = component.OperationAsync();
        await component.Foreground.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var disposed = component.DisposeAsync().AsTask();
        var concurrentDisposal = component.DisposeAsync().AsTask();
        try
        {
            if (start)
            {
                Assert.Same(component.StopEntered.Task, await Task.WhenAny(component.StopEntered.Task, disposed).WaitAsync(TimeSpan.FromSeconds(10), Token));
                await component.Background.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                component.Background.Release.TrySetResult();
            }
            await component.Foreground.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(disposed.IsCompleted);
            Assert.False(concurrentDisposal.IsCompleted);
            Assert.Equal(0, component.Disposals);
        }
        finally { component.ReleaseForCleanup(); }

        var error = await Assert.ThrowsAsync<AggregateException>(() => disposed.WaitAsync(TimeSpan.FromSeconds(10), Token));
        if (failOperation)
            Assert.Contains(operationError, error.Flatten().InnerExceptions);
        if (failRuntime)
            Assert.Contains(runtimeError, error.Flatten().InnerExceptions);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await concurrentDisposal.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(component.Foreground.Completed);
        Assert.False(component.CleanupBeforeDrain);
        Assert.Equal(ComponentState.Disposed, component.LifecycleState);
        Assert.Equal(start ? 1 : 0, component.Stops);
        Assert.Equal(1, component.Disposals);
        await component.DisposeAsync();
        Assert.Equal(1, component.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => component.OperationAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => component.StartAsync(Token));
    }

    [Fact]
    public async Task Failed_start_preserves_start_and_cleanup_errors_after_draining_background()
    {
        using var account = new AccountSigner();
        await using var component = new Component(account);
        var startError = new IOException("startup failed");
        var callbackError = new InvalidOperationException("runtime callback failed");
        component.StartError = startError;
        component.RuntimeError = callbackError;
        await component.InitializeAsync(Token);
        var started = component.StartAsync(Token);
        try
        {
            Assert.Same(component.StopEntered.Task, await Task.WhenAny(component.StopEntered.Task, started).WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.False(started.IsCompleted);
        }
        finally { component.ReleaseForCleanup(); }

        var error = await Assert.ThrowsAsync<AggregateException>(() => started.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Contains(startError, error.Flatten().InnerExceptions);
        Assert.Contains(callbackError, error.Flatten().InnerExceptions);
        Assert.True(component.Background.Completed);
        Assert.Equal(ComponentState.Stopped, component.LifecycleState);
        Assert.Throws<InvalidOperationException>(() => component.RuntimeToken);
        await component.StartAsync(Token);
        await component.StopAsync(Token);
    }

    [Fact]
    public async Task Disposal_preserves_all_phase_errors_and_allows_failed_resource_cleanup_to_be_retried()
    {
        using var account = new AccountSigner();
        await using var component = new Component(account);
        var callbackError = new InvalidOperationException("runtime callback failed");
        var stopError = new IOException("stop hook failed");
        var disposalError = new IOException("resource cleanup failed");
        component.RuntimeError = callbackError;
        component.StopError = stopError;
        component.DisposalError = disposalError;
        component.Background.Release.TrySetResult();
        await component.InitializeAsync(Token);
        await component.StartAsync(Token);
        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => component.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal(3, error.Flatten().InnerExceptions.Count);
            Assert.Contains(callbackError, error.Flatten().InnerExceptions);
            Assert.Contains(stopError, error.Flatten().InnerExceptions);
            Assert.Contains(disposalError, error.Flatten().InnerExceptions);
            Assert.True(component.Background.Completed);
            Assert.False(component.CleanupBeforeDrain);
            Assert.Equal(1, component.Disposals);
            Assert.NotEqual(ComponentState.Disposed, component.LifecycleState);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => component.OperationAsync());
        }
        finally { component.ReleaseForCleanup(); }

        await component.DisposeAsync();
        Assert.Equal(ComponentState.Disposed, component.LifecycleState);
        Assert.Equal(1, component.Stops);
        Assert.Equal(2, component.Disposals);
    }

    [Fact]
    public async Task A_single_resource_cleanup_error_keeps_its_original_type_and_can_be_retried()
    {
        using var account = new AccountSigner();
        await using var component = new Component(account);
        var expected = new IOException("resource cleanup failed");
        component.DisposalError = expected;
        try
        {
            Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => component.DisposeAsync().AsTask()));
            Assert.Contains("DisposeAsyncCore", expected.StackTrace);
            Assert.NotEqual(ComponentState.Disposed, component.LifecycleState);
        }
        finally { component.ReleaseForCleanup(); }

        await component.DisposeAsync();
        Assert.Equal(ComponentState.Disposed, component.LifecycleState);
        Assert.Equal(2, component.Disposals);
    }
}
