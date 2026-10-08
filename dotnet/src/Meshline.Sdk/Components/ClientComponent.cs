using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Components;

/// <summary>
/// Provides initialization, start/stop, operation tracking, and asynchronous disposal for SDK components.
/// </summary>
/// <remarks>
/// Initialize before invoking component operations. Starting begins background processing; stopping allows a later restart. Disposal cancels and drains tracked operations. Event handlers execute on the raising thread and UI consumers must marshal updates as needed.
/// </remarks>
public abstract class ClientComponent : IAsyncDisposable
{
    /// <summary>
    /// Occurs when the component lifecycle state changes.
    /// </summary>
    public event EventHandler<ComponentStateChangedEventArgs>? StateChanged;
    /// <summary>
    /// Occurs when background processing reports a failure; awaited operation failures propagate to their caller.
    /// </summary>
    public event EventHandler<BackgroundErrorEventArgs>? BackgroundError;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    readonly Lock _operationGate = new();
    CancellationTokenSource? _runtime;
    TaskCompletionSource? _drained;
    int _operations;
    bool _disposing;

    /// <summary>
    /// The network and account options supplied when this component was constructed.
    /// </summary>
    public ClientOptions Options { get; }
    /// <summary>
    /// The network reference and registry contract identifying this Meshline network.
    /// </summary>
    public NetworkContext Context => Options.Context;
    /// <summary>
    /// The component's current initialization and runtime state.
    /// </summary>
    public ComponentState LifecycleState { get; private set; }
    /// <summary>
    /// The current runtime token, canceled when background work stops or the component is disposed.
    /// </summary>
    protected CancellationToken RuntimeCancellationToken => _runtime?.Token ?? throw new InvalidOperationException("The component has not started.");

    /// <summary>
    /// Initializes a new instance of <see cref="ClientComponent"/>.
    /// </summary>
    /// <param name="options">The network and account configuration for this component.</param>
    /// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null. The <paramref name="options"/> argument is null.</exception>
    /// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
    /// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
    protected ClientComponent(ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
    }

    /// <summary>
    /// Initializes component state once and transitions the component to the stopped state.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// This method does not create or migrate the database. Initialization failures propagate and leave initialization incomplete so the caller can correct the cause and retry.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="ObjectDisposedException">This component, or a dependency initialized by its initialization hook, has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The component initialization hook rejects the database binding or an unmet dependency initialization requirement.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="InvalidDataException">A component initialization hook finds inconsistent persisted device state.</exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ComponentStateChangedEventArgs change;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(LifecycleState == ComponentState.Disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (LifecycleState != ComponentState.Uninitialized)
                return;

            await OnInitializeAsync(cancellationToken).ConfigureAwait(false);
            change = new(LifecycleState, ComponentState.Stopped);
            LifecycleState = ComponentState.Stopped;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        StateChanged?.Invoke(this, change);
    }

    /// <summary>
    /// Initializes component-specific state before the lifecycle transitions to stopped.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    protected virtual Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private protected async Task<DatabaseBinding> EnsureDatabaseBindingAsync(MeshlineDbContext database, CancellationToken cancellationToken)
    {
        var binding = await database.Bindings.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            binding = new DatabaseBinding { Id = 1, Context = Options.Context.ToString(), AccountId = Options.AccountId };
            database.Bindings.Add(binding);
            try
            {
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                database.ChangeTracker.Clear();
                binding = await database.Bindings.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (binding is null)
                    throw;
            }
        }
        if (binding.Id != 1 || binding.Context != Options.Context.ToString() || binding.AccountId != Options.AccountId)
            throw new InvalidOperationException("The database belongs to another network or account.");
        return binding;
    }

    /// <summary>
    /// Raises the background error event with the failed activity and its diagnostic exception.
    /// </summary>
    /// <param name="operation">The background activity that failed.</param>
    /// <param name="resource">The associated resource identifier, or <see langword="null"/> when none applies.</param>
    /// <param name="error">The failure to report.</param>
    protected void ReportBackgroundError(BackgroundOperation operation, string? resource, Exception error) =>
        BackgroundError?.Invoke(this, new(operation, resource, error));

    internal void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(LifecycleState == ComponentState.Disposed, this);
        if (LifecycleState == ComponentState.Uninitialized)
            throw new InvalidOperationException("InitializeAsync must complete before using the component.");
    }

    /// <summary>
    /// Starts background work after initialization; repeated calls while running have no effect.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// The caller token controls startup. Once startup completes, background work uses the component runtime token and ends through stop or disposal. A client device must already have published authorization before its device component can start.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">The component has been disposed, or disposal has begun while registering an operation.</exception>
    /// <exception cref="InvalidOperationException">Initialization is incomplete, or a component startup hook lacks the required local device, route, or published authorization.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="UnauthorizedAccessException">A component startup hook cannot authorize the local device or an account synchronization message.</exception>
    /// <exception cref="ArgumentException">A startup hook encounters invalid input while resuming a pending account operation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A startup hook resumes an account operation whose validity interval or revision is outside the supported range.</exception>
    /// <exception cref="NotSupportedException">A startup hook encounters an account namespace unsupported by this SDK.</exception>
    /// <exception cref="AggregateException">Startup and cleanup both fail, or a runtime cancellation callback throws during cleanup.</exception>
    public virtual async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ComponentStateChangedEventArgs change;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (LifecycleState == ComponentState.Running)
                return;
            _runtime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runtime.Token);
            try
            {
                await OnStartAsync(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (Exception startError)
            {
                try { await EndRuntimeAsync().ConfigureAwait(false); }
                catch (Exception cleanupError) { throw new AggregateException(startError, cleanupError); }
                throw;
            }
            change = new(LifecycleState, ComponentState.Running);
            LifecycleState = ComponentState.Running;
        }
        finally
        {
            _lifecycleGate.Release();
        }
        StateChanged?.Invoke(this, change);
    }

    /// <summary>
    /// Stops background work and subscriptions while leaving the component available to start again.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel waiting to begin stopping; active shutdown is drained without cancellation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// The cancellation token controls waiting to enter the lifecycle operation. Once stopping begins, runtime shutdown is drained without that token. The application-owned relay pool remains available to other components.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> is canceled while waiting to enter the lifecycle transition.</exception>
    /// <exception cref="ObjectDisposedException">This component, or a dependency stopped by its shutdown hook, has been disposed.</exception>
    /// <exception cref="AggregateException">A runtime cancellation callback fails, or multiple shutdown failures are reported after runtime cleanup.</exception>
    public virtual async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ComponentStateChangedEventArgs change;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(LifecycleState == ComponentState.Disposed, this);
            if (LifecycleState != ComponentState.Running)
                return;
            LifecycleState = ComponentState.Stopping;
            try
            {
                await EndRuntimeAsync().ConfigureAwait(false);
            }
            finally
            {
                LifecycleState = ComponentState.Stopped;
            }
            change = new(ComponentState.Running, ComponentState.Stopped);
        }
        finally
        {
            _lifecycleGate.Release();
        }
        StateChanged?.Invoke(this, change);
    }

    /// <summary>
    /// Starts component-specific background work for the current runtime.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    protected virtual Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>
    /// Drains component-specific background work after the runtime token has been canceled.
    /// </summary>
    /// <returns>A task that completes when the operation finishes.</returns>
    protected virtual Task OnStopAsync() => Task.CompletedTask;

    async Task EndRuntimeAsync()
    {
        if (_runtime is null)
            return;
        List<Exception> failures = [];
        try
        {
            // Cancellation failures must not prevent the hook from draining background work.
            try { await _runtime.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            try { await OnStopAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        finally
        {
            _runtime.Dispose();
            _runtime = null;
        }
        ThrowCleanupFailures(failures);
    }

    /// <summary>
    /// Tracks a foreground operation and links its cancellation token to component disposal.
    /// </summary>
    /// <param name="cancellationToken">The caller's token, replaced with a token linked to the component's lifetime.</param>
    /// <returns>A scope that releases the linked token source and operation registration when disposed.</returns>
    /// <remarks>
    /// Dispose the returned scope when the operation finishes. This method replaces the supplied token with a linked token; it does not perform initialization checks for the derived component.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> is already canceled.</exception>
    /// <exception cref="ObjectDisposedException">The component has been disposed, or disposal has begun while registering an operation.</exception>
    protected IDisposable BeginOperation(ref CancellationToken cancellationToken)
    {
        lock (_operationGate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            cancellationToken.ThrowIfCancellationRequested();
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cancellationToken = lifetime.Token;
            _operations++;
            return new ClientOperation(this, lifetime);
        }
    }

    /// <summary>
    /// Cancels and drains active operations, stops background work, and releases component-owned resources.
    /// </summary>
    /// <returns>A value task that completes when owned resources and active work have been released.</returns>
    /// <exception cref="SqliteException">A component cleanup hook fails to access SQLite while persisting pending local observations.</exception>
    /// <exception cref="DbUpdateException">A component cleanup hook cannot save its pending local observations.</exception>
    /// <exception cref="AggregateException">A lifetime cancellation callback fails, or multiple cleanup failures are reported after the remaining cleanup steps have been attempted.</exception>
    public async ValueTask DisposeAsync()
    {
        ComponentStateChangedEventArgs change;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (LifecycleState == ComponentState.Disposed)
                return;

            Task drained;
            lock (_operationGate)
            {
                _disposing = true;
                drained = _operations == 0 ? Task.CompletedTask : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            List<Exception> failures = [];
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            try { await EndRuntimeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            await drained.ConfigureAwait(false);
            try { await DisposeAsyncCore().ConfigureAwait(false); }
            catch (Exception error)
            {
                failures.Add(error);
                // Keep disposal retryable when the resource cleanup hook itself fails.
                ThrowCleanupFailures(failures);
            }
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
            change = new(LifecycleState, ComponentState.Disposed);
            LifecycleState = ComponentState.Disposed;
            ThrowCleanupFailures(failures);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        StateChanged?.Invoke(this, change);
    }

    static void ThrowCleanupFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }

    /// <summary>
    /// Releases component-specific resources after background work and tracked operations have ended.
    /// </summary>
    /// <returns>A value task that completes when resource cleanup finishes.</returns>
    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    sealed class ClientOperation(ClientComponent component, CancellationTokenSource lifetime) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lifetime.Dispose();
            lock (component._operationGate)
                if (--component._operations == 0)
                    component._drained?.TrySetResult();
        }
    }
}
