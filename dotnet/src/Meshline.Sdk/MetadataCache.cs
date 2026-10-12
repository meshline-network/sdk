using System.Runtime.ExceptionServices;

namespace Meshline;

// One object's refresh coordination. Owners retain protocol validation, storage and key selection.
// No timers: only ReadAsync can start work. Refresh failures never replace a usable value.
sealed class MetadataCache<T>(T? value = null, DateTimeOffset? refreshedAt = null) where T : class
{
    readonly Lock gate = new();
    readonly TimeProvider clock = Clock.Provider;
    CancellationTokenSource lifetime = new();
    T? value = value;
    DateTimeOffset refreshAfter = refreshedAt?.AddHours(1) ?? default;
    DateTimeOffset retryAfter;
    Task<Result>? refresh;
    Task? stopping;
    long generation;
    long freshnessVersion;
    bool disposed;

    sealed record Result(T? Value, ExceptionDispatchInfo? Failure = null);
    public T? Value { get { lock (gate) return value; } }

    public async Task DrainCurrentAsync(CancellationToken cancellationToken)
    {
        Task<Result>? pending;
        lock (gate) pending = refresh is { IsCompleted: false } ? refresh : null;
        if (pending is not null)
        {
            var result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Failure?.SourceException is OperationCanceledException) result.Failure.Throw();
        }
    }

    public void Reject(Exception error)
    {
        lock (gate)
        {
            refreshAfter = retryAfter = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
            if (refresh is not { IsCompleted: false }) refresh = Task.FromResult(new Result(null, ExceptionDispatchInfo.Capture(error)));
        }
    }

    public void Invalidate() { lock (gate) { value = null; generation++; } }

    public Task<T?> ReadAsync(Func<T, bool> usable, Func<CancellationToken, Task<T?>> fetch,
        Action<Exception> report, CancellationToken cancellationToken, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<Result> pending;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = clock.GetUtcNow();
            if (!force && value is { } cached && usable(cached))
            {
                if (stopping is null && now >= refreshAfter) Start();
                return Task.FromResult<T?>(cached);
            }
            if (stopping is not null) throw new OperationCanceledException("Metadata refreshes are stopping.");
            // Forcing an update bypasses freshness, but repeated failed required reads
            // still share the failure cooldown rather than starting a request storm.
            pending = now < retryAfter && refresh is { } failed ? failed : Start();

            Task<Result> Start()
            {
                if (refresh is { IsCompleted: false }) return refresh;
                retryAfter = default;
                return refresh = RunAsync(generation, freshnessVersion, usable, fetch, report, lifetime.Token);
            }
        }
        return AwaitAsync(pending, cancellationToken);
    }

    static async Task<T?> AwaitAsync(Task<Result> pending, CancellationToken cancellationToken)
    {
        var result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        result.Failure?.Throw();
        return result.Value;
    }

    async Task<Result> RunAsync(long observed, long observedFreshness, Func<T, bool> usable, Func<CancellationToken, Task<T?>> fetch,
        Action<Exception> report, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var result = await fetch(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (generation != observed) result = value;
                else if (result is not null) value = result;
                if (result is null || !usable(result))
                    refreshAfter = retryAfter = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
                else if (freshnessVersion == observedFreshness)
                {
                    refreshAfter = clock.GetUtcNow() + TimeSpan.FromHours(1);
                    retryAfter = default;
                }
            }
            return new(result);
        }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                lock (gate)
                    // An old failure must not shorten the freshness of a later verified publication.
                    if (freshnessVersion == observedFreshness)
                        refreshAfter = retryAfter = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
                try { report(error); }
                catch (Exception observer) { error = new AggregateException(error, observer); }
            }
            return new(null, ExceptionDispatchInfo.Capture(error));
        }
    }

    public void RecordSuccess(DateTimeOffset completed)
    {
        lock (gate) { refreshAfter = completed + TimeSpan.FromHours(1); freshnessVersion++; }
    }

    // Only a successful remote update supplies a freshness time; protocol lifetimes never change.
    public void Publish(T replacement, DateTimeOffset? completed = null)
    {
        lock (gate)
        {
            value = replacement; generation++; retryAfter = default;
            if (completed is { } at) RecordSuccess(at);
        }
    }

    public Task StopAsync(bool dispose = false)
    {
        lock (gate)
        {
            if (disposed) return stopping ?? Task.CompletedTask;
            disposed |= dispose;
            return stopping ??= DrainAsync();
        }
    }

    async Task DrainAsync()
    {
        await Task.Yield();
        Task<Result>? pending;
        lock (gate) pending = refresh;
        try { await Task.WhenAll(lifetime.CancelAsync(), (Task?)pending ?? Task.CompletedTask).ConfigureAwait(false); }
        finally
        {
            lock (gate)
            {
                lifetime.Dispose(); lifetime = new(); stopping = null; refresh = null; retryAfter = default;
                if (disposed) { value = null; lifetime.Dispose(); }
            }
        }
    }
}
