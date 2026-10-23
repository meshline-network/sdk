using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Meshline.Components;

sealed partial class DeviceManager
{
    readonly Lock _stateCacheGate = new();
    bool _stateStopping;
    readonly Dictionary<string, AccountDeviceState> _verifiedStates = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> _stateRefreshedAt = new(StringComparer.Ordinal);
    readonly Dictionary<StateQueryKey, MetadataCache<AccountDeviceState>> _stateQueries = [];
    readonly record struct StateQueryKey(string Account, string Relay, string Access);
    enum StateReadMode { Cached, Refresh, RefreshAfterCurrent }

    async Task LoadStoredStateAsync(string accountId, CancellationToken cancellationToken)
    {
        long revision;
        lock (_stateCacheGate) revision = _verifiedStates.GetValueOrDefault(accountId)?.Revision ?? -1;
        await using var database = new MeshlineDbContext(databaseOptions);
        // Other components commit complete signed states in their own transactions. Observe only
        // committed newer records here, without requiring those writers to know about this cache.
        var row = await database.DeviceStates.AsNoTracking()
            .SingleOrDefaultAsync(value => value.AccountId == accountId && value.Revision > revision, cancellationToken).ConfigureAwait(false);
        if (row is null) return;
        var state = ProtocolModel.FromJson<AccountDeviceState>(row.DocumentJson);
        if (state is null || state.Account != accountId || state.Revision != row.Revision || state.Validate(Context) is not null)
            throw new InvalidDataException("The persisted device state is invalid.");
        CacheDeviceState(state);
    }

    async Task ValidateRefreshedRevisionAsync(AccountDeviceState state, string relayId, CancellationToken cancellationToken)
    {
        long? announced;
        if (state.Account == Options.AccountId)
            announced = _requiredDeviceRevisions.TryGetValue(relayId, out var revision) ? revision : null;
        else
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            announced = await database.Contacts.AsNoTracking().Where(value => value.AccountId == state.Account)
                .Select(value => value.RequiredDeviceRevision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        // A hint does not invalidate a usable snapshot. An update below the hint is incomplete,
        // however, and follows the failed-refresh cooldown instead of reporting fresh metadata.
        if (announced is { } required && state.Revision < required)
            throw new InvalidDataException("The device state has not reached the announced revision.");
    }

    async Task<AccountDeviceState?> ReadDeviceStateAsync(string accountId, TypedProtocolModel? authorization, string relayId, bool useAccount,
        StateReadMode mode, CancellationToken cancellationToken)
    {
        // Access proof and signing identity bound request sharing; all scopes share the account's revision floor.
        var key = new StateQueryKey(accountId, relayId, $"{useAccount}\n{Local?.ToJson()}\n{authorization?.ToJson()}");
        MetadataCache<AccountDeviceState> cache;
        lock (_stateCacheGate)
        {
            if (_stateStopping) throw new OperationCanceledException("Device-state refreshes are stopping.");
            if (!_stateQueries.TryGetValue(key, out cache!))
            {
                // Public queries must establish access in their own context. A successful query
                // keeps its result in _stateQueries; private results never seed a public query.
                var known = accountId == Options.AccountId || authorization is not null
                    ? _verifiedStates.GetValueOrDefault(accountId) : null;
                DateTimeOffset? when = _stateRefreshedAt.TryGetValue(accountId, out var completed) ? completed : null;
                _stateQueries.Add(key, cache = new(known, when));
            }
        }
        bool Usable(AccountDeviceState state) => AuthorizationValid(state, authorization)
            && (accountId != Options.AccountId || Local is not { } local || state.ValidateDeviceAuthorization(local.GetDeviceId(Context), Context) is null);

        if (mode == StateReadMode.RefreshAfterCurrent) await cache.DrainCurrentAsync(cancellationToken).ConfigureAwait(false);
        var result = await cache.ReadAsync(Usable, async token =>
        {
            using var operation = BeginOperation(ref token);
            try
            {
                // Ordinary reads never escalate device access into wallet authorization.
                // Explicit account setup uses its own account-capable read path.
                var state = await ResolveDeviceStateCoreAsync(accountId, authorization, relayId, useAccount, token, allowAccountFallback: false).ConfigureAwait(false);
                if (state is not null)
                {
                    await ValidateRefreshedRevisionAsync(state, relayId, token).ConfigureAwait(false);
                    // Compatible sessions and signed proofs reuse the successful refresh time.
                    CacheDeviceState(state, Clock.UtcNow);
                }
                return state;
            }
            catch (RelayException error) when (accountId != Options.AccountId && error.Error.Code is "forbidden" or "unauthorized")
            {
                cache.Invalidate();
                throw;
            }
        }, error => ReportBackgroundError(BackgroundOperation.Synchronize, accountId, error), cancellationToken, force: mode != StateReadMode.Cached).ConfigureAwait(false);
        if (result is not null)
        {
            try
            {
                ValidateStateAuthorization(result, authorization);
            }
            catch (Exception error) when (error is InvalidDataException or CryptographicException)
            {
                cache.Reject(error); throw;
            }
        }
        return result;
    }

    bool AuthorizationValid(AccountDeviceState state, TypedProtocolModel? authorization)
    {
        try { ValidateStateAuthorization(state, authorization); return true; }
        catch (Exception error) when (error is InvalidDataException or CryptographicException) { return false; }
    }

    void CacheDeviceState(AccountDeviceState state, DateTimeOffset? refreshedAt = null)
    {
        lock (_stateCacheGate)
        {
            if (_verifiedStates.TryGetValue(state.Account, out var previous) && previous.Revision > state.Revision) return;
            _verifiedStates[state.Account] = state;
            if (refreshedAt is { } at) _stateRefreshedAt[state.Account] = at;
            foreach (var (key, cache) in _stateQueries)
                if (key.Account == state.Account && (key.Account == Options.AccountId || cache.Value is not null))
                {
                    if (cache.Value?.Revision != state.Revision) cache.Publish(state, refreshedAt);
                    else if (refreshedAt is { } completed) cache.RecordSuccess(completed);
                }
        }
    }

    async Task StopStateQueriesAsync(bool dispose = false)
    {
        MetadataCache<AccountDeviceState>[] caches;
        lock (_stateCacheGate) { _stateStopping = true; caches = [.. _stateQueries.Values]; }
        try { await Task.WhenAll(caches.Select(cache => cache.StopAsync(dispose))).ConfigureAwait(false); }
        finally
        {
            lock (_stateCacheGate)
            {
                _stateStopping = dispose;
                if (dispose) { _stateQueries.Clear(); _verifiedStates.Clear(); _stateRefreshedAt.Clear(); }
            }
        }
    }

    /// <summary>Awaits device-state refresh, bypassing the success interval but retaining the one-minute failure cooldown.</summary>
    /// <inheritdoc cref="GetDeviceStateAsync(string, CancellationToken)" path="param|returns|exception"/>
    public async Task<AccountDeviceState?> RefreshDeviceStateAsync(string? accountId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await ResolveDeviceStateAsync(accountId ?? Options.AccountId, null, cancellationToken, mode: StateReadMode.Refresh).ConfigureAwait(false);
    }

    /// <summary>Awaits device-state refresh with the supplied grant, bypassing the success interval but retaining the one-minute failure cooldown.</summary>
    /// <inheritdoc cref="GetDeviceStateAsync(ContactGrant, CancellationToken)" path="param|returns|exception"/>
    public async Task<AccountDeviceState?> RefreshDeviceStateAsync(ContactGrant grant, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await ResolveDeviceStateAsync(grant.Grantor, grant, cancellationToken, mode: StateReadMode.Refresh).ConfigureAwait(false);
    }

    /// <summary>Awaits device-state refresh with the supplied invitation, bypassing the success interval but retaining the one-minute failure cooldown.</summary>
    /// <inheritdoc cref="GetDeviceStateAsync(ContactInvite, CancellationToken)" path="param|returns|exception"/>
    public async Task<AccountDeviceState?> RefreshDeviceStateAsync(ContactInvite invite, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await ResolveDeviceStateAsync(invite.Inviter, invite, cancellationToken, mode: StateReadMode.Refresh).ConfigureAwait(false);
    }
}
