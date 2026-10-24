using Meshline.Identity;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Meshline.Components;

sealed partial class ProfileManager
{
    readonly Lock _profileCacheGate = new();
    bool _profilesStopping;
    long _profilePublication;
    readonly SemaphoreSlim _profileSaveGate = new(1, 1);
    readonly Dictionary<string, MetadataCache<ProfileResolveResult>> _profiles = new(StringComparer.Ordinal);

    async Task LoadProfileCacheAsync(MeshlineDbContext database, CancellationToken cancellationToken)
    {
        foreach (var row in await database.AccountProfiles.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = new ProfileResolveResult { Profile = ProtocolModel.FromJson<AccountProfile>(row.DocumentJson)!, SignerCertificate = ProtocolModel.FromJson<DeviceCertificate>(row.SignerCertificateJson)! };
            ValidateCachedProfile(result, row.AccountId);
            _profiles[row.AccountId] = new(result);
            if (row.AccountId == AccountId) Profile = result.Profile;
        }
    }

    void ValidateCachedProfile(ProfileResolveResult result, string accountId)
    {
        if (result.Validate(Context) is not null || result.Profile.Account != accountId || result.SignerCertificate.Account != accountId
            || !Ed25519.Verify(result.Profile.DeviceSignature.AsSpan(), result.SignerCertificate.SigningPublicKey.AsSpan(), result.Profile.GetSigningInput(Context)))
            throw new InvalidDataException("The persisted profile evidence is invalid.");
    }

    MetadataCache<ProfileResolveResult> ProfileCache(string accountId)
    {
        lock (_profileCacheGate)
        {
            if (_profilesStopping) throw new OperationCanceledException("Profile refreshes are stopping.");
            if (!_profiles.TryGetValue(accountId, out var cache)) _profiles.Add(accountId, cache = new());
            return cache;
        }
    }

    Task<ProfileResolveResult?> ReadProfileAsync(string accountId, bool force, CancellationToken cancellationToken) =>
        ProfileCache(accountId).ReadAsync(_ => true, async token =>
        {
            using var operation = BeginOperation(ref token);
            var publication = Interlocked.Read(ref _profilePublication);
            var result = await ResolveProfileAsync(accountId, token).ConfigureAwait(false);
            if (result is not null) await SaveProfileAsync(result, token, publication).ConfigureAwait(false);
            return result;
        }, error => ReportBackgroundError(BackgroundOperation.Synchronize, accountId, error), cancellationToken, force);

    async Task SaveProfileAsync(ProfileResolveResult result, CancellationToken cancellationToken, long? publication = null, bool published = false)
    {
        var changed = false;
        await _profileSaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (publication is not null && publication != _profilePublication && result.Profile.Account == AccountId && result.Profile.UpdatedAt <= Profile?.UpdatedAt) return;
            await StoreProfileAsync(result, cancellationToken).ConfigureAwait(false);
            if (published) Interlocked.Increment(ref _profilePublication);
            ProfileCache(result.Profile.Account).Publish(result, published ? Clock.UtcNow : null);
            if (result.Profile.Account == AccountId) { changed = Profile?.ToJson() != result.Profile.ToJson(); Profile = result.Profile; }
        }
        finally { _profileSaveGate.Release(); }
        if (changed) ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Awaits profile refresh, bypassing the success interval but retaining the one-minute failure cooldown.</summary>
    /// <inheritdoc cref="GetProfileAsync" path="/*[self::param or self::returns or self::exception]"/>
    public async Task<AccountProfile?> RefreshProfileAsync(string? accountId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized(); using var operation = BeginOperation(ref cancellationToken);
        accountId ??= AccountId;
        if (AccountAdapter.ValidateAccountId(accountId) is { } invalid) throw new ArgumentException(invalid.Message, nameof(accountId));
        return (await ReadProfileAsync(accountId, true, cancellationToken).ConfigureAwait(false))?.Profile;
    }

    async Task StopProfilesAsync(bool dispose = false)
    {
        Task pending;
        lock (_profileCacheGate) { _profilesStopping = true; pending = Task.WhenAll(_profiles.Values.Select(cache => cache.StopAsync(dispose))); }
        try { await pending.ConfigureAwait(false); }
        finally { lock (_profileCacheGate) _profilesStopping = dispose; }
    }
    /// <inheritdoc/>
    protected override Task OnStopAsync() => StopProfilesAsync();
    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore() { await StopProfilesAsync(dispose: true).ConfigureAwait(false); await base.DisposeAsyncCore().ConfigureAwait(false); }
}
