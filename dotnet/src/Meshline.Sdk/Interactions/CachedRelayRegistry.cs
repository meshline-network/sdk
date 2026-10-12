using Meshline.Identity;
using Meshline.Models;
using Meshline.Models.Registry;
using Meshline.Validation;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Meshline.Interactions;

/// <summary>
/// Adds optional in-memory, demand-triggered caching to a relay registry.
/// </summary>
/// <remarks>
/// Reads return cached entries immediately while a due refresh runs in the background. Successful refreshes
/// defer the next refresh for one hour; failures and missing entries defer retries for one minute.
/// No timer runs without read demand. These intervals do not define registration validity.
/// Each instance is isolated to its source registry and network, and may be shared by multiple pools.
/// Dispose the wrapper after its consumers; the source remains owned by the caller.
/// </remarks>
public sealed class CachedRelayRegistry : IRelayRegistry, IAsyncDisposable
{
    readonly IRelayRegistry source;
    readonly Lock gate = new();
    readonly MetadataCache<RelayEntry[]> directory = new();
    readonly Dictionary<string, MetadataCache<Lookup>> entries = new(StringComparer.Ordinal);
    readonly Dictionary<string, RelayEntry> known = new(StringComparer.Ordinal);
    readonly HashSet<string> absent = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> changedAt = new(StringComparer.Ordinal);
    long generation;
    bool disposed;
    sealed record Lookup(RelayEntry? Entry);
    /// <inheritdoc cref="IRelayRegistry.Context"/>
    public NetworkContext Context { get; }

    /// <summary>
    /// Initializes an optional cache around the supplied registry.
    /// </summary>
    /// <param name="source">The caller-owned registry, whose network and registry context must remain unchanged.</param>
    /// <exception cref="ArgumentNullException">The source registry is null.</exception>
    public CachedRelayRegistry(IRelayRegistry source)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        Context = source.Context;
    }

    static void Report(Exception error) => Trace.TraceWarning("Registry metadata refresh failed: {0}", error);

    /// <inheritdoc cref="IRelayRegistry.GetRelayAsync"/>
    /// <remarks>Caller cancellation only ends that caller's wait. Refresh failures preserve cached entries and are reported through <see cref="Trace"/>.</remarks>
    /// <exception cref="ArgumentException">The relay identifier is invalid.</exception>
    /// <exception cref="InvalidDataException">The source returns invalid or regressed registration data and no cached result is available.</exception>
    /// <exception cref="ObjectDisposedException">The wrapper has been disposed.</exception>
    public async Task<RelayEntry?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        if (RelayIdentity.ValidateRelayId(relayId) is { } violation) throw new ArgumentException(violation.Message, nameof(relayId));
        if (directory.Value is { } snapshot && snapshot.Any(value => value.RelayId == relayId))
        {
            _ = await ReadDirectoryAsync(cancellationToken).ConfigureAwait(false);
            lock (gate) return absent.Contains(relayId) ? null : known.GetValueOrDefault(relayId);
        }
        MetadataCache<Lookup> cache;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(relayId, out cache!)) entries.Add(relayId, cache = new());
        }
        var result = await cache.ReadAsync(value => value.Entry is not null, async token =>
        {
            long observed;
            lock (gate) observed = generation;
            var entry = await source.GetRelayAsync(relayId, token).ConfigureAwait(false);
            if (entry is not null)
            {
                if (entry.RelayId != relayId) throw new InvalidDataException("Registry returned another relay.");
                Validate(entry);
                lock (gate) Accept(entry, observed);
            }
            else lock (gate) MarkAbsent(relayId, observed);
            lock (gate) return new Lookup(absent.Contains(relayId) ? null : known.GetValueOrDefault(relayId));
        }, Report, cancellationToken).ConfigureAwait(false);
        lock (gate) return absent.Contains(relayId) ? null : known.GetValueOrDefault(relayId) ?? result?.Entry;
    }

    /// <inheritdoc cref="IRelayRegistry.GetRelaysAsync"/>
    /// <remarks>A source enumeration is validated in full before publication. Failed refreshes retain the previous snapshot.</remarks>
    /// <exception cref="InvalidDataException">The source returns invalid, duplicate or regressed registrations and no cached snapshot is available.</exception>
    /// <exception cref="ObjectDisposedException">The wrapper has been disposed.</exception>
    public async IAsyncEnumerable<RelayEntry> GetRelaysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = await ReadDirectoryAsync(cancellationToken).ConfigureAwait(false);
        RelayEntry[] snapshot;
        lock (gate) snapshot = known.Values.Where(entry => !absent.Contains(entry.RelayId)).ToArray();
        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested(); yield return entry;
        }
    }

    async Task<RelayEntry[]> ReadDirectoryAsync(CancellationToken cancellationToken)
    {
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        return await directory.ReadAsync(_ => true, async token =>
        {
            long observed;
            lock (gate) observed = generation;
            var snapshot = new Dictionary<string, RelayEntry>(StringComparer.Ordinal);
            await foreach (var entry in source.GetRelaysAsync(token).ConfigureAwait(false))
            {
                Validate(entry);
                if (!snapshot.TryAdd(entry.RelayId, entry)) throw new InvalidDataException("Registry directory contains duplicate relays.");
            }
            lock (gate)
            {
                // Validate the whole snapshot before publishing any part of it.
                foreach (var entry in snapshot.Values) if (!SupersededAbsence(entry.RelayId, observed)) CheckVersion(entry);
                foreach (var entry in snapshot.Values) Accept(entry, observed);
                foreach (var id in known.Keys) if (!snapshot.ContainsKey(id)) MarkAbsent(id, observed);
            }
            return snapshot.Values.ToArray();
        }, Report, cancellationToken).ConfigureAwait(false) ?? [];
    }

    static void Validate(RelayEntry entry)
    {
        if (RelayIdentity.ValidateRelayId(entry.RelayId) is not null || !Enum.IsDefined(entry.Status)
            || RelayEndpointValidator.Validate(entry.Endpoint, out var kind, out _) is not null || kind != RelayEndpointKind.Https)
            throw new InvalidDataException("Registry entry is invalid.");
    }

    void CheckVersion(RelayEntry entry)
    {
        if (known.TryGetValue(entry.RelayId, out var previous) && entry.UpdatedAt < previous.UpdatedAt)
            throw new InvalidDataException("Registry entry is older than the known update.");
    }

    bool SupersededAbsence(string id, long observed) => absent.Contains(id) && changedAt.GetValueOrDefault(id) > observed;

    void Accept(RelayEntry entry, long observed)
    {
        if (SupersededAbsence(entry.RelayId, observed)) return;
        // Registry timestamps are not unique revisions: sequential updates in one block may tie.
        if (known.TryGetValue(entry.RelayId, out var previous) && changedAt.GetValueOrDefault(entry.RelayId) > observed && entry.UpdatedAt <= previous.UpdatedAt) return;
        CheckVersion(entry); known[entry.RelayId] = entry; absent.Remove(entry.RelayId);
        changedAt[entry.RelayId] = ++generation;
    }

    void MarkAbsent(string id, long observed)
    {
        if (changedAt.GetValueOrDefault(id) > observed) return;
        absent.Add(id); changedAt[id] = ++generation;
    }

    /// <summary>
    /// Cancels and drains cache refreshes without disposing the source registry.
    /// </summary>
    /// <returns>A value task that completes after the wrapper's pending refreshes have ended.</returns>
    /// <exception cref="AggregateException">A cancellation callback throws while refreshes are canceled.</exception>
    public async ValueTask DisposeAsync()
    {
        MetadataCache<Lookup>[] pending;
        lock (gate) { disposed = true; pending = [.. entries.Values]; }
        await Task.WhenAll(pending.Select(cache => cache.StopAsync(dispose: true)).Append(directory.StopAsync(dispose: true))).ConfigureAwait(false);
    }
}
