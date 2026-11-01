using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Meshline.Components;

// Coordinates admission, supersession and retries. Each manager owns publication
// storage, execution and its protocol-specific completion/retry rules.
abstract class StateUpdateCoordinator(DatabaseOptions options)
{
    readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, AttemptRun> active = new(StringComparer.Ordinal);
    readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly ConcurrentDictionary<Task, byte> workers = new();
    // Backoff counts restart with this coordinator. Keep the computed schedule in
    // memory too, so a failed deadline write cannot cause immediate repeated sends.
    readonly ConcurrentDictionary<string, (long Generation, int Failures, DateTimeOffset? NextAttemptAt)> retryStates = new(StringComparer.Ordinal);
    Task? runtime;

    protected abstract string Operation { get; }
    protected abstract Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token);
    protected abstract IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent);
    protected abstract Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token);
    protected abstract Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token);
    protected abstract void Report(string resource, Exception error);
    protected virtual void BeforeMerge(JsonObject previous, JsonObject patch) { }
    protected virtual DateTimeOffset? GetFollowUpAt(object? result) => null;

    protected StatePublication Publication(string resource, IStatePublication state) => new(resource, Operation + ":" + resource, state);

    protected virtual bool CanRetry(Exception error) =>
        error is HttpRequestException or TimeoutException or OperationCanceledException or SqliteException or DbUpdateConcurrencyException
        or DbUpdateException { InnerException: SqliteException }
        or RelayException { Error.Code: "stale_state" or "state_conflict" or "clock_skew" or "rate_limited" or "internal_error" or "bad_gateway" or "temporarily_unavailable" or "unauthorized" or "route_stale" or "target_not_local" };

    protected virtual DateTimeOffset? GetNextAttemptAt(IStatePublication publication, Exception error, int failures)
    {
        if (!CanRetry(error)) return null;
        var delay = Math.Min(30, 1 << Math.Min(5, failures - 1));
        var hint = (error as RelayException)?.Error.GetRetryAfter() ?? 0;
        var seconds = Math.Max(delay, hint);
        var now = Clock.UtcNow;
        return seconds > (DateTimeOffset.MaxValue - now).Ticks / TimeSpan.TicksPerSecond
            ? DateTimeOffset.MaxValue : now.AddSeconds(seconds);
    }

    SemaphoreSlim Gate(string key) => gates.GetOrAdd(key, static _ => new(1, 1));

    DateTimeOffset? ScheduledAt(string key, IStatePublication publication) =>
        retryStates.TryGetValue(key, out var retry) && retry.Generation == publication.Generation
            ? retry.NextAttemptAt : publication.NextAttemptAt;

    public async Task<T> SubmitAsync<T>(string resource, JsonObject patch, CancellationToken token)
    {
        var key = Operation + ":" + resource;
        var gate = Gate(key);
        await gate.WaitAsync(token).ConfigureAwait(false);
        AttemptRun run;
        try
        {
            await using var database = new MeshlineDbContext(options);
            await database.Database.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false);
            await using var enlisted = await database.Database.UseTransactionAsync(transaction, token).ConfigureAwait(false);
            var record = await FindAsync(database, resource, token).ConfigureAwait(false);
            var intent = record is { Pending: true, IntentJson: not null } ? JsonNode.Parse(record.IntentJson)!.AsObject() : new JsonObject();
            BeforeMerge(intent, patch);
            foreach (var pair in patch) intent[pair.Key] = pair.Value?.DeepClone();
            record ??= Create(database, resource, intent);
            record.RequestState = record.Pending ? record.RequestState.Supersede() : PublicationRequestState.None;
            record.IntentJson = intent.ToJsonString();
            record.Generation = checked(record.Generation + 1);
            record.Pending = true;
            record.NextAttemptAt = Clock.UtcNow;
            await database.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            retryStates.TryRemove(key, out _);
            if (active.TryRemove(key, out var previous)) previous.Supersede(key);
            run = new(new(resource, key, record), token);
            active[key] = run;
        }
        finally { gate.Release(); }
        _ = ObserveWorkAsync(run.Completion.Task);
        Track(ExecuteAsync(run));
        return (T)(await run.Completion.Task.WaitAsync(token).ConfigureAwait(false))!;
    }

    // Adopt pending publications prepared outside this coordinator in place.
    public async Task ImportAsync(string resource, JsonObject intent, CancellationToken token)
    {
        await using var database = new MeshlineDbContext(options);
        await database.Database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await using var enlisted = await database.Database.UseTransactionAsync(transaction, token).ConfigureAwait(false);
        var record = await FindAsync(database, resource, token).ConfigureAwait(false);
        if (record is { Pending: true, IntentJson: null, RequestJson: not null })
        {
            record.IntentJson = intent.ToJsonString();
            record.Generation = checked(record.Generation + 1);
            record.RequestState = PublicationRequestState.Prepared;
            record.NextAttemptAt = Clock.UtcNow;
            await database.SaveChangesAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public void Start(CancellationToken token) => runtime = RecoverAsync(token);
    public async Task StopAsync()
    {
        if (runtime is not null) await runtime.ConfigureAwait(false);
        runtime = null;
    }

    public async Task DrainAsync()
    {
        while (!workers.IsEmpty) await Task.WhenAll(workers.Keys.Select(ObserveWorkAsync)).ConfigureAwait(false);
    }

    async Task RecoverAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                try
                {
                    await using var database = new MeshlineDbContext(options);
                    var now = Clock.UtcNow;
                    var rows = (await ReadPendingAsync(database, token).ConfigureAwait(false)).Where(value => ScheduledAt(value.Key, value.State) <= now);
                    foreach (var row in rows)
                    {
                        var gate = Gate(row.Key);
                        AttemptRun run;
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            if (active.ContainsKey(row.Key)) continue;
                            // Re-read after taking the admission gate; a new call may have replaced this scan.
                            await using var current = new MeshlineDbContext(options);
                            var latest = await FindAsync(current, row.Resource, token).ConfigureAwait(false);
                            if (latest is not { Pending: true, NextAttemptAt: not null, IntentJson: not null }
                                || ScheduledAt(row.Key, latest) is not { } due || due > Clock.UtcNow) continue;
                            run = new AttemptRun(new(row.Resource, row.Key, latest), token);
                            active[row.Key] = run;
                        }
                        finally { gate.Release(); }
                        Track(ExecuteAsync(run, background: true));
                        _ = ObserveAsync(run, token);
                    }
                    await using var schedule = new MeshlineDbContext(options);
                    var waiting = await ReadPendingAsync(schedule, token).ConfigureAwait(false);
                    var next = waiting.Where(value => !active.ContainsKey(value.Key)).Select(value => ScheduledAt(value.Key, value.State)).Min();
                    if (next is null) await wake.Reader.ReadAsync(token).ConfigureAwait(false);
                    else
                    {
                        var delay = next.Value - Clock.UtcNow;
                        if (delay <= TimeSpan.Zero) continue;
                        // Keep the durable deadline, but wait in bounded timer intervals.
                        if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        await Task.WhenAny(wake.Reader.ReadAsync(wait.Token).AsTask(), Task.Delay(delay, Clock.Provider, wait.Token)).ConfigureAwait(false);
                        await wait.CancelAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    Report(Operation, error);
                    await Task.Delay(TimeSpan.FromSeconds(1), Clock.Provider, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            var pending = active.Values.Where(value => value.OwnerToken == token).Select(value => value.Completion.Task).ToArray();
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* Each failure is observed separately. */ }
        }
    }

    async Task ObserveAsync(AttemptRun run, CancellationToken token)
    {
        try { await run.Completion.Task.ConfigureAwait(false); }
        catch (StateUpdateSupersededException) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Report(run.Record.Key, error); }
    }

    async Task ExecuteAsync(AttemptRun run, bool background = false)
    {
        var attempt = new StateUpdateAttempt(this, run.Record, background);
        try
        {
            // WaitAsync ends the old call even when a transport or signer ignores cancellation.
            var work = PublishAsync(attempt, run.Cancellation.Token);
            Track(work);
            var result = await work.WaitAsync(run.Cancellation.Token).ConfigureAwait(false);
            await CurrentAsync(run.Record, async () =>
            {
                await using var database = new MeshlineDbContext(options);
                var next = GetFollowUpAt(result);
                var row = (await FindAsync(database, run.Record.Resource, CancellationToken.None).ConfigureAwait(false))!;
                // Authoritative discovery may already have confirmed this generation.
                var pending = row.Pending && next is not null;
                row.Pending = pending;
                if (!pending) row.IntentJson = null;
                row.NextAttemptAt = pending ? next : null;
                await database.SaveChangesAsync().ConfigureAwait(false);
                retryStates.TryRemove(run.Record.Key, out _);
                run.Completion.TrySetResult(result);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try
            {
                await CurrentAsync(run.Record, async () =>
                {
                    await using var database = new MeshlineDbContext(options);
                    var row = (await FindAsync(database, run.Record.Resource, CancellationToken.None).ConfigureAwait(false))!;
                    if (!row.Pending) return;
                    var previous = retryStates.GetValueOrDefault(run.Record.Key);
                    var failures = previous.Generation == run.Record.State.Generation ? Math.Min(previous.Failures + 1, 30) : 1;
                    // Pending + no deadline means retained intent with automatic retry paused.
                    row.NextAttemptAt = GetNextAttemptAt(row, error, failures);
                    retryStates[run.Record.Key] = (run.Record.State.Generation, failures, row.NextAttemptAt);
                    await database.SaveChangesAsync().ConfigureAwait(false);
                    if (row.NextAttemptAt is null) retryStates.TryRemove(run.Record.Key, out _);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (StateUpdateSupersededException) { error = new StateUpdateSupersededException(run.Record.Key); }
            catch (Exception storageError) { error = new AggregateException(error, storageError); }
            run.Completion.TrySetException(error);
        }
        finally
        {
            active.TryRemove(new KeyValuePair<string, AttemptRun>(run.Record.Key, run));
            await run.DisposeAsync().ConfigureAwait(false);
            wake.Writer.TryWrite(true);
        }
    }

    void Track(Task work)
    {
        workers.TryAdd(work, 0);
        _ = ReleaseWorkAsync(work);
    }

    async Task ReleaseWorkAsync(Task work)
    {
        await ObserveWorkAsync(work).ConfigureAwait(false);
        workers.TryRemove(work, out _);
    }

    static async Task ObserveWorkAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { /* The foreground or recovery waiter owns the outcome, even after cancellation. */ }
    }

    internal async Task CurrentAsync(StatePublication expected, Func<Task> action, CancellationToken token)
    {
        var gate = Gate(expected.Key);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(options);
            var current = await FindAsync(database, expected.Resource, token).ConfigureAwait(false);
            // Confirmation can clear Pending while this generation's response is still
            // returning. Only a newer durable generation supersedes the call.
            if (current is null || current.Generation != expected.State.Generation)
                throw new StateUpdateSupersededException(expected.Key);
            token.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal Task SaveRequestAsync(StatePublication expected, string json, CancellationToken token) => CurrentAsync(expected, async () =>
    {
        await using var database = new MeshlineDbContext(options);
        var row = (await FindAsync(database, expected.Resource, token).ConfigureAwait(false))!;
        row.RequestJson = json;
        row.RequestState = PublicationRequestState.Prepared;
        await database.SaveChangesAsync(token).ConfigureAwait(false);
        expected.State.RequestJson = json;
        expected.State.RequestState = PublicationRequestState.Prepared;
    }, token);

    internal Task InvalidateRequestAsync(StatePublication expected, string json) => CurrentAsync(expected, async () =>
    {
        await using var database = new MeshlineDbContext(options);
        var row = (await FindAsync(database, expected.Resource, CancellationToken.None).ConfigureAwait(false))!;
        if (row.RequestJson == json && row.RequestState.IsCurrent())
        {
            row.RequestState = PublicationRequestState.Rejected;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        if (expected.State.RequestJson == json) expected.State.RequestState = PublicationRequestState.Rejected;
    }, CancellationToken.None);

    sealed class AttemptRun(StatePublication record, CancellationToken token) : IAsyncDisposable
    {
        readonly Lock completionGate = new();
        Task cancellation = Task.CompletedTask;
        public StatePublication Record { get; } = record;
        public CancellationToken OwnerToken { get; } = token;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Supersede(string key)
        {
            lock (completionGate)
            {
                if (!Completion.TrySetException(new StateUpdateSupersededException(key))) return;
                cancellation = Cancellation.CancelAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task pending;
            // Supersession completes the caller before starting cancellation. Do not
            // dispose its source or finish draining while those callbacks still run.
            lock (completionGate) pending = cancellation;
            await ObserveWorkAsync(pending).ConfigureAwait(false);
            Cancellation.Dispose();
        }
    }
}

// An attempt's in-memory snapshot of an existing publication row. It has no table.
sealed record StatePublication(string Resource, string Key, IStatePublication State);

sealed class StateUpdateAttempt(StateUpdateCoordinator owner, StatePublication record, bool background)
{
    public bool IsBackground { get; } = background;
    public JsonObject Intent { get; } = JsonNode.Parse(record.State.IntentJson!)!.AsObject();
    public long Generation => record.State.Generation;
    public string? RequestJson => record.State.RequestState == PublicationRequestState.Prepared ? record.State.RequestJson : null;
    public Task SaveRequestAsync(string json, CancellationToken token) => owner.SaveRequestAsync(record, json, token);
    // Device publication saves its request and revision floor in the guarded
    // transaction. Keep that request available to this attempt's conflict retries.
    public void RecordRequest(string json)
    {
        record.State.RequestJson = json;
        record.State.RequestState = PublicationRequestState.Prepared;
    }
    public Task InvalidateRequestAsync(string json) => owner.InvalidateRequestAsync(record, json);
    public Task CurrentAsync(Func<Task> action, CancellationToken token) => owner.CurrentAsync(record, action, token);
    public Task CheckAsync(CancellationToken token) => CurrentAsync(() => Task.CompletedTask, token);
}
