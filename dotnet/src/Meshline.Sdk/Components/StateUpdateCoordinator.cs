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
    readonly ConcurrentDictionary<SharedOperation<object?>, byte> outcomes = new();
    readonly ConcurrentQueue<Exception> cleanupFailures = new();
    // Backoff counts restart with this coordinator. Keep the computed schedule in
    // memory too, so a failed outcome read or write cannot cause repeated sends.
    readonly ConcurrentDictionary<string, (long Generation, int Failures, DateTimeOffset? NextAttemptAt, StateUpdateFailure Failure)> retryStates = new(StringComparer.Ordinal);
    Task? runtime;

    protected abstract string Operation { get; }
    protected abstract Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token);
    protected abstract IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent);
    protected abstract Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token);
    protected abstract Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token);
    protected abstract void Report(string resource, Exception error);
    protected virtual void BeforeMerge(JsonObject previous, JsonObject patch) { }
    protected virtual DateTimeOffset? GetFollowUpAt(object? result) => null;
    protected virtual void SetError(IStatePublication publication, StateUpdateFailure? failure) { }
    protected virtual void OnChanged(string resource) { }

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

    // Authoritative discovery can finish intent outside an active attempt. Share
    // admission's gate so its generation change cannot race a guarded write.
    protected async Task WithPublicationLockAsync(string resource, Func<Task> action, CancellationToken token)
    {
        var gate = Gate(Operation + ":" + resource);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    DateTimeOffset? ScheduledAt(string key, IStatePublication publication) =>
        retryStates.TryGetValue(key, out var retry) && retry.Generation == publication.Generation
            ? retry.NextAttemptAt : publication.NextAttemptAt;

    void ApplyFailure(IStatePublication row, StateUpdateFailure failure)
    {
        if (failure.RejectedRequestJson is { } rejected && row.RequestJson == rejected && row.RequestState.IsCurrent())
            row.RequestState = PublicationRequestState.Rejected;
        SetError(row, failure);
    }

    void RestoreFailure(string key, IStatePublication? row)
    {
        if (row is { Pending: true } && retryStates.TryGetValue(key, out var retry) && retry.Generation == row.Generation)
            ApplyFailure(row, retry.Failure);
    }

    protected async Task<T> ReadAsync<T>(string resource, Func<IStatePublication?, bool, DateTimeOffset?, T> read, CancellationToken token)
    {
        var key = Operation + ":" + resource;
        var gate = Gate(key);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(options);
            var row = await FindAsync(database, resource, token).ConfigureAwait(false);
            var publishing = row is { Pending: true } && active.TryGetValue(key, out var run)
                && run.Record.State.Generation == row.Generation && !run.Completion.Task.IsCompleted;
            RestoreFailure(key, row);
            return read(row, publishing, row is null ? null : ScheduledAt(key, row));
        }
        finally { gate.Release(); }
    }

    public Task<T> SubmitAsync<T>(string resource, JsonObject patch, CancellationToken token) => SubmitCoreAsync<T>(resource, patch, token, resume: false);

    // Initialization may adopt existing intent, but must never replace newer edits.
    // The owner waits for its publication to drain on cancellation, as setup does
    // for its other protocol steps. Joining somebody else's attempt only waits.
    public Task<T> ResumeAsync<T>(string resource, JsonObject initial, CancellationToken token) => SubmitCoreAsync<T>(resource, initial, token, resume: true);

    async Task<T> SubmitCoreAsync<T>(string resource, JsonObject patch, CancellationToken token, bool resume)
    {
        var key = Operation + ":" + resource;
        var gate = Gate(key);
        await gate.WaitAsync(token).ConfigureAwait(false);
        AttemptRun run;
        var ownsRun = true;
        try
        {
            await using var database = new MeshlineDbContext(options);
            await database.Database.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false);
            await using var enlisted = await database.Database.UseTransactionAsync(transaction, token).ConfigureAwait(false);
            var record = await FindAsync(database, resource, token).ConfigureAwait(false);
            RestoreFailure(key, record);
            if (resume && record is { Pending: true, IntentJson: not null } && active.TryGetValue(key, out var existing)
                && existing.Record.State.Generation == record.Generation && !existing.Completion.Task.IsCompleted)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                run = existing;
                ownsRun = false;
            }
            else
            {
                var retain = resume && record is { Pending: true, IntentJson: not null };
                if (!retain)
                {
                    var intent = record is { Pending: true, IntentJson: not null } ? JsonNode.Parse(record.IntentJson)!.AsObject() : new JsonObject();
                    BeforeMerge(intent, patch);
                    foreach (var pair in patch) intent[pair.Key] = pair.Value?.DeepClone();
                    record ??= Create(database, resource, intent);
                    record.RequestState = record.Pending ? record.RequestState.Supersede() : PublicationRequestState.None;
                    record.IntentJson = intent.ToJsonString();
                    record.Generation = checked(record.Generation + 1);
                    SetError(record, null);
                }
                record!.Pending = true;
                record.NextAttemptAt = Clock.UtcNow;
                await database.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                run = new(new(resource, key, record), token, waitForPublication: resume);
            }
            if (ownsRun)
            {
                if (!resume) retryStates.TryRemove(key, out _);
                if (active.TryRemove(key, out var previous)) previous.Supersede(key);
                Remember(run.Outcome);
                active[key] = run;
            }
        }
        finally { gate.Release(); }
        var waiting = run.Outcome.WaitAsync(ownsRun && resume ? CancellationToken.None : token);
        if (ownsRun) Track(ExecuteAsync(run));
        return (T)(await waiting.ConfigureAwait(false))!;
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
        var stopping = runtime;
        List<Exception> failures = [];
        try { if (stopping is not null) await stopping.ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        finally { _ = Interlocked.CompareExchange(ref runtime, null, stopping); }
        await DrainOutcomesAsync(failures, completedOnly: true).ConfigureAwait(false);
        ThrowCleanupFailures(failures);
    }

    public async Task DrainAsync()
    {
        while (!workers.IsEmpty) await Task.WhenAll(workers.Keys.Select(ObserveWorkAsync)).ConfigureAwait(false);
        List<Exception> failures = [];
        await DrainOutcomesAsync(failures).ConfigureAwait(false);
        ThrowCleanupFailures(failures);
    }

    void Remember(SharedOperation<object?> outcome)
    {
        foreach (var previous in outcomes.Keys)
            if (previous.CanForget) outcomes.TryRemove(previous, out _);
        outcomes.TryAdd(outcome, 0);
    }

    async Task DrainOutcomesAsync(List<Exception> failures, bool completedOnly = false)
    {
        foreach (var outcome in outcomes.Keys)
        {
            if (completedOnly && !outcome.IsCompleted) continue;
            try { await outcome.DrainAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            finally { if (outcome.CanForget) outcomes.TryRemove(outcome, out _); }
        }
    }

    void ThrowCleanupFailures(List<Exception> failures)
    {
        while (cleanupFailures.TryDequeue(out var error)) failures.Add(error);
        BackgroundFailure.ThrowIfAny(failures);
    }

    async Task RecoverAsync(CancellationToken token)
    {
        HashSet<Task> observers = [];
        ConcurrentQueue<Exception> failures = new();
        try
        {
            while (failures.IsEmpty)
            {
                observers.RemoveWhere(static observer => observer.IsCompleted);
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
                            RestoreFailure(row.Key, latest);
                            run = new AttemptRun(new(row.Resource, row.Key, latest), token);
                            Remember(run.Outcome);
                            active[row.Key] = run;
                        }
                        finally { gate.Release(); }
                        observers.Add(ObserveAsync(run, token, failures));
                        Track(ExecuteAsync(run, background: true));
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
                        await BackgroundFailure.CompleteAsync(wait.CancelAsync()).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                }
                catch (Exception error)
                {
                    BackgroundFailure.ThrowIfCanceled(error, token);
                    Report(Operation, error);
                    await Task.Delay(TimeSpan.FromSeconds(1), Clock.Provider, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested && !BackgroundFailure.IsRequired(error)) { }
        catch (Exception error) { failures.Enqueue(error); }
        finally
        {
            var pending = active.Values.Where(value => value.OwnerToken == token).Select(value => value.Completion.Task).ToArray();
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* Each failure is observed separately. */ }
            // A completed attempt can still be delivering its error notification.
            await Task.WhenAll(observers).ConfigureAwait(false);
        }
        BackgroundFailure.ThrowIfAny(failures.ToArray());
    }

    async Task ObserveAsync(AttemptRun run, CancellationToken token, ConcurrentQueue<Exception> failures)
    {
        try { await run.Outcome.WaitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (StateUpdateSupersededException) { }
        catch (OperationCanceledException error) when (token.IsCancellationRequested && !BackgroundFailure.IsRequired(error)) { }
        catch (Exception error)
        {
            if (BackgroundFailure.IsRequired(error))
            {
                failures.Enqueue(error);
                wake.Writer.TryWrite(true);
            }
            try { Report(run.Record.Key, error); }
            catch (Exception observer)
            {
                failures.Enqueue(observer);
                wake.Writer.TryWrite(true);
            }
        }
        finally { await run.Drained.Task.ConfigureAwait(false); }
    }

    async Task ExecuteAsync(AttemptRun run, bool background = false)
    {
        var attempt = new StateUpdateAttempt(this, run.Record, background);
        try
        {
            OnChanged(run.Record.Resource);
            // Ordinary updates stop waiting even when signing or transport ignores cancellation.
            var work = PublishAsync(attempt, run.Cancellation.Token);
            var publication = new SharedOperation<object?>(work);
            Remember(publication);
            Track(work);
            var result = await publication.WaitAsync(run.WaitForPublication ? CancellationToken.None : run.Cancellation.Token).ConfigureAwait(false);
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
                SetError(row, null);
                await database.SaveChangesAsync().ConfigureAwait(false);
                retryStates.TryRemove(run.Record.Key, out _);
                run.Completion.TrySetResult(result);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var failure = new StateUpdateFailure(error, Clock.UtcNow, attempt.RejectedRequestJson);
            try
            {
                await SaveFailureAsync(run, failure).ConfigureAwait(false);
            }
            catch (StateUpdateSupersededException) when (!BackgroundFailure.IsRequired(error)) { error = new StateUpdateSupersededException(run.Record.Key); }
            catch (StateUpdateSupersededException) { }
            catch (Exception storageError) { error = new AggregateException(error, storageError); }
            if (!run.Completion.TrySetException(error) && BackgroundFailure.IsRequired(error)) cleanupFailures.Enqueue(error);
        }
        finally
        {
            try { if (active.TryRemove(new KeyValuePair<string, AttemptRun>(run.Record.Key, run))) OnChanged(run.Record.Resource); }
            catch (Exception error) { cleanupFailures.Enqueue(error); }
            try { await run.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanupFailures.Enqueue(error); }
            run.Drained.TrySetResult();
            wake.Writer.TryWrite(true);
        }
    }

    async Task SaveFailureAsync(AttemptRun run, StateUpdateFailure failure)
    {
        var record = run.Record;
        var gate = Gate(record.Key);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!active.TryGetValue(record.Key, out var current) || current != run)
                throw new StateUpdateSupersededException(record.Key);
            var previous = retryStates.GetValueOrDefault(record.Key);
            var failures = previous.Generation == record.State.Generation ? Math.Min(previous.Failures + 1, 30) : 1;
            DateTimeOffset? Remember(IStatePublication state)
            {
                var next = GetNextAttemptAt(state, failure.Error, failures);
                retryStates[record.Key] = (record.State.Generation, failures, next, failure);
                return next;
            }
            // Keep a generation-scoped fallback before any storage access. Discovery
            // may have retired it; the durable generation and Pending still govern reads.
            Remember(record.State);
            await using var database = new MeshlineDbContext(options);
            var row = await FindAsync(database, record.Resource, CancellationToken.None).ConfigureAwait(false);
            if (row is null || row.Generation != record.State.Generation)
            {
                retryStates.TryRemove(record.Key, out _);
                throw new StateUpdateSupersededException(record.Key);
            }
            if (!row.Pending) { retryStates.TryRemove(record.Key, out _); return; }
            row.NextAttemptAt = Remember(row);
            ApplyFailure(row, failure);
            await database.SaveChangesAsync().ConfigureAwait(false);
            if (row.NextAttemptAt is null) retryStates.TryRemove(record.Key, out _);
        }
        finally { gate.Release(); }
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
        catch { /* Shared outcomes retain failures not delivered to a waiter. */ }
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

    sealed class AttemptRun : IAsyncDisposable
    {
        readonly Lock completionGate = new();
        Task cancellation = Task.CompletedTask;
        public StatePublication Record { get; }
        public bool WaitForPublication { get; }
        public CancellationToken OwnerToken { get; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SharedOperation<object?> Outcome { get; }
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AttemptRun(StatePublication record, CancellationToken token, bool waitForPublication = false)
        {
            Record = record;
            WaitForPublication = waitForPublication;
            OwnerToken = token;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Outcome = new(Completion.Task);
        }
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
            try { await pending.ConfigureAwait(false); }
            finally { Cancellation.Dispose(); }
        }
    }
}

// An attempt's in-memory snapshot of an existing publication row. It has no table.
sealed record StatePublication(string Resource, string Key, IStatePublication State);

// Retained with the retry schedule even if persisting the latest outcome fails.
sealed record StateUpdateFailure(Exception Error, DateTimeOffset OccurredAt, string? RejectedRequestJson = null);

sealed class StateUpdateAttempt(StateUpdateCoordinator owner, StatePublication record, bool background)
{
    public bool IsBackground { get; } = background;
    public JsonObject Intent { get; } = JsonNode.Parse(record.State.IntentJson!)!.AsObject();
    public long Generation => record.State.Generation;
    public string? RequestJson => record.State.RequestState == PublicationRequestState.Prepared ? record.State.RequestJson : null;
    public string? RejectedRequestJson { get; private set; } = record.State.RequestState is PublicationRequestState.Rejected or PublicationRequestState.SupersededRejected ? record.State.RequestJson : null;
    public async Task SaveRequestAsync(string json, CancellationToken token)
    {
        await owner.SaveRequestAsync(record, json, token).ConfigureAwait(false);
        RejectedRequestJson = null;
    }
    // Save the known rejection together with diagnostics and its retry deadline.
    public void RecordRejection(string json) => RejectedRequestJson = json;
    // Keep requests saved by manager-owned transactions available for retries
    // and failure diagnostics even when subsequent storage access fails.
    public void RecordRequest(string json)
    {
        record.State.RequestJson = json;
        record.State.RequestState = PublicationRequestState.Prepared;
        RejectedRequestJson = null;
    }
    public Task InvalidateRequestAsync(string json) => owner.InvalidateRequestAsync(record, json);
    public Task CurrentAsync(Func<Task> action, CancellationToken token) => owner.CurrentAsync(record, action, token);
    public Task CheckAsync(CancellationToken token) => CurrentAsync(() => Task.CompletedTask, token);
}
