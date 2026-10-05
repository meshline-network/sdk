using Meshline.Models.Client;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Components;

internal sealed record SyncBlock(ResourceSyncBlockReason Reason, Exception? Error = null);

// Runtime observations only. A generation prevents an older pass from overwriting a newer pass or stop.
internal sealed class ResourceSyncTracker
{
    readonly Lock _gate = new();
    readonly Dictionary<string, (long Generation, ResourceSyncStatus Status, bool PreserveOnStop)> _resources = new(StringComparer.Ordinal);
    long _generation;

    public ResourceSyncStatus Get(string resource)
    {
        lock (_gate) return GetCore(resource);
    }

    ResourceSyncStatus GetCore(string resource) => _resources.TryGetValue(resource, out var entry) ? entry.Status : new() { Resource = resource, State = ResourceSyncState.Idle };

    public void ObserveGap(string resource, Action<ResourceSyncStatus>? changed = null)
    {
        ResourceSyncStatus status;
        lock (_gate)
        {
            var current = GetCore(resource);
            if (current.HasRetentionGap) return;
            status = Copy(current, current.State, current.BlockReason, current.Error, gap: true);
            var entry = _resources.GetValueOrDefault(resource);
            _resources[resource] = (entry.Generation, status, entry.PreserveOnStop);
        }
        changed?.Invoke(status);
    }

    public async Task<ResourceSyncStatus> RunAsync(string resource, Func<Task<SyncBlock?>> action, Action<ResourceSyncStatus> changed, CancellationToken cancellationToken, bool preserveOnStop = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        ResourceSyncStatus status;
        lock (_gate)
        {
            generation = ++_generation;
            status = Copy(GetCore(resource), ResourceSyncState.Synchronizing);
            _resources[resource] = (generation, status, preserveOnStop);
        }
        changed(status);
        try
        {
            var block = await action().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Finish(resource, generation, block is null ? ResourceSyncState.CaughtUp : ResourceSyncState.Blocked, block, changed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish(resource, generation, ResourceSyncState.Idle, null, changed);
            throw;
        }
        catch (Exception error)
        {
            Finish(resource, generation, ResourceSyncState.Blocked, new(Classify(error), error), changed);
            throw;
        }
    }

    public void Block(string resource, Exception error, Action<ResourceSyncStatus> changed)
    {
        ResourceSyncStatus status;
        lock (_gate)
        {
            status = Copy(GetCore(resource), ResourceSyncState.Blocked, Classify(error), error);
            _resources[resource] = (++_generation, status, false);
        }
        changed(status);
    }

    public void Stop(Action<ResourceSyncStatus> changed, bool resetAll = false)
    {
        List<ResourceSyncStatus> changes = [];
        lock (_gate)
        {
            foreach (var resource in _resources.Keys.ToArray())
            {
                // Explicit synchronization has its own lifetime, independent of background work.
                if (!resetAll && _resources[resource].PreserveOnStop) continue;
                var current = GetCore(resource);
                var status = Copy(current, ResourceSyncState.Idle);
                _resources[resource] = (++_generation, status, false);
                if (current.State != ResourceSyncState.Idle) changes.Add(status);
            }
        }
        foreach (var status in changes) changed(status);
    }

    ResourceSyncStatus Finish(string resource, long generation, ResourceSyncState state, SyncBlock? block, Action<ResourceSyncStatus> changed)
    {
        ResourceSyncStatus status;
        lock (_gate)
        {
            status = Copy(GetCore(resource), state, block?.Reason, block?.Error, completed: state == ResourceSyncState.CaughtUp);
            if (!_resources.TryGetValue(resource, out var entry) || entry.Generation != generation) return status;
            _resources[resource] = (generation, status, entry.PreserveOnStop);
        }
        changed(status);
        return status;
    }

    static ResourceSyncStatus Copy(ResourceSyncStatus current, ResourceSyncState state, ResourceSyncBlockReason? reason = null, Exception? error = null, bool? gap = null, bool completed = false) => new()
    {
        Resource = current.Resource,
        State = state,
        LastSynchronizedAt = completed ? Clock.UtcNow : current.LastSynchronizedAt,
        BlockReason = reason,
        Error = error,
        HasRetentionGap = gap ?? current.HasRetentionGap
    };

    internal static ResourceSyncBlockReason Classify(Exception error) => error switch
    {
        RelayException relay => relay.Error.Code switch
        {
            "unauthorized" or "device_unknown" => ResourceSyncBlockReason.Authentication,
            "forbidden" => ResourceSyncBlockReason.Permission,
            "invalid_signature" => ResourceSyncBlockReason.Verification,
            "history_unavailable" => ResourceSyncBlockReason.HistoryUnavailable,
            "bad_gateway" or "temporarily_unavailable" or "rate_limited" => ResourceSyncBlockReason.Connection,
            _ => ResourceSyncBlockReason.Unknown
        },
        SqliteException or DbUpdateException => ResourceSyncBlockReason.Storage,
        HttpRequestException or TimeoutException or OperationCanceledException => ResourceSyncBlockReason.Connection,
        UnauthorizedAccessException => ResourceSyncBlockReason.Permission,
        InvalidDataException or JsonException or CryptographicException => ResourceSyncBlockReason.Verification,
        _ => ResourceSyncBlockReason.Unknown
    };
}
