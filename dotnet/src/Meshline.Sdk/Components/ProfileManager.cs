using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Components;

/// <summary>
/// Resolves, verifies, caches, and publishes account profiles.
/// </summary>
/// <param name="options">The network and account configuration for this component.</param>
/// <param name="databaseOptions">The SQLite database configuration; create its parent directory and apply migrations before initialization.</param>
/// <param name="relayClients">The shared relay pool. The application owns it and must dispose it after all dependent components.</param>
/// <param name="accountManager">The account component sharing this network, account, database, and relay pool.</param>
/// <param name="deviceSigner">The device signer used for profile signatures and device-authenticated relay access.</param>
/// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null. The <paramref name="options"/> argument is null.</exception>
/// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
/// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
public sealed class ProfileManager(ClientOptions options, DatabaseOptions databaseOptions, RelayClientPool relayClients, AccountManager accountManager, IDeviceSigner deviceSigner) : ClientComponent(options)
{
    /// <summary>
    /// Occurs when the current account's locally known profile changes.
    /// </summary>
    public event EventHandler? ProfileChanged;

    private readonly SemaphoreSlim _profileGate = new(1, 1);

    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public string AccountId => Options.AccountId;
    /// <summary>
    /// The current account's profile, or <see langword="null"/> when it is unavailable.
    /// </summary>
    public AccountProfile? Profile { get; private set; }

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">The database is bound to another network or account, or a required dependency has not been initialized.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        await EnsureDatabaseBindingAsync(database, cancellationToken).ConfigureAwait(false);
        var profile = await database.AccountProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == AccountId, cancellationToken).ConfigureAwait(false);
        Profile = profile is null ? null : ProtocolModel.FromJson<AccountProfile>(profile.DocumentJson);
    }

    /// <summary>
    /// Resolves and verifies an account profile and updates its local cache.
    /// </summary>
    /// <param name="accountId">The target account identifier, or <see langword="null"/> for the current account.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The resolved profile, or <see langword="null"/> when no profile is available.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The account has no resolvable home route or a usable device-authenticated session cannot be established.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The requested account identifier is invalid.</exception>
    /// <exception cref="NotSupportedException">The requested account uses an unsupported namespace.</exception>
    public async Task<AccountProfile?> GetProfileAsync(string? accountId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        accountId ??= AccountId;
        if (AccountAdapter.ValidateAccountId(accountId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(accountId));
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AccountProfile? profile;
        bool changed = false;
        try
        {
            var result = await ResolveProfileAsync(accountId, cancellationToken).ConfigureAwait(false);
            profile = result?.Profile;
            if (result is not null)
                await SaveProfileAsync(result, cancellationToken).ConfigureAwait(false);
            if (accountId == AccountId)
            {
                changed = Profile?.ToJson() != profile?.ToJson();
                Profile = profile;
            }
        }
        finally
        {
            _profileGate.Release();
        }
        if (changed)
            ProfileChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    /// <summary>
    /// Applies profile field updates, signs the result with the local device, and publishes it.
    /// </summary>
    /// <param name="update">The field assignments and deletions to apply; unspecified fields remain unchanged.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The signed profile published after applying the updates.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The account has no resolvable home route or a usable device-authenticated session cannot be established. A pending profile publication conflicts with the requested content.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The profile belongs to another account, a required public-discovery field is deleted, or the resulting profile fails validation.</exception>
    public Task<AccountProfile> UpdateProfileAsync(ProfileUpdate update, CancellationToken cancellationToken = default) =>
        PublishProfileCoreAsync(update, null, cancellationToken);

    /// <summary>
    /// Republishes the newest available profile for this account, including an optional supplied snapshot.
    /// </summary>
    /// <param name="profile">An optional profile snapshot for this account to consider alongside cached state.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The republished profile, or <see langword="null"/> if no profile was available to publish.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The account has no resolvable home route or a usable device-authenticated session cannot be established. A pending profile publication conflicts with the requested content.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The profile belongs to another account, a required public-discovery field is deleted, or the resulting profile fails validation.</exception>
    public async Task<AccountProfile?> PublishProfileAsync(AccountProfile? profile = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (profile is not null && profile.Account != AccountId)
            throw new ArgumentException("The profile must belong to the current account.", nameof(profile));
        profile = await ReadLatestProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile is null ? null : await PublishProfileCoreAsync(new(), profile, cancellationToken).ConfigureAwait(false);
    }

    async Task<AccountProfile?> ReadLatestProfileAsync(AccountProfile? fallback, CancellationToken cancellationToken)
    {
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            var local = await database.AccountProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == AccountId, cancellationToken).ConfigureAwait(false);
            var pending = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "profile.publish" && value.Pending, cancellationToken).ConfigureAwait(false);
            var json = pending is not null && (local is null || pending.Revision >= local.UpdatedAt) ? pending.DocumentJson : local?.DocumentJson;
            var profile = json is null ? null : ProtocolModel.FromJson<AccountProfile>(json);
            return profile is not null && (fallback is null || profile.UpdatedAt >= fallback.UpdatedAt) ? profile : fallback;
        }
        finally { _profileGate.Release(); }
    }

    async Task<AccountProfile> PublishProfileCoreAsync(ProfileUpdate update, AccountProfile? restored, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        if (update.PublicDiscovery.IsDeleted)
            throw new ArgumentException("PublicDiscovery cannot be deleted.", nameof(update));
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AccountProfile request;
        bool changed;
        try
        {
            var certificate = deviceSigner.Certificate;
            var route = await accountManager.GetRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account route could not be found. Establish or recover the account explicitly.");
            var relayId = route.RelayId;
            await using var database = new MeshlineDbContext(databaseOptions);
            var signed = await database.SignedRequests.SingleOrDefaultAsync(value => value.Method == "profile.publish", cancellationToken).ConfigureAwait(false);
            if (signed is { Pending: true } && (restored is null || signed.RelayId == relayId))
            {
                request = ProtocolModel.FromJson<AccountProfile>(signed.DocumentJson)!;
                if (signed.RelayId != relayId || Apply(update.Nickname, request.Nickname) != request.Nickname || Apply(update.Bio, request.Bio) != request.Bio
                    || Apply(update.Avatar, request.Avatar)?.ToJson() != request.Avatar?.ToJson() || update.PublicDiscovery.IsSpecified && update.PublicDiscovery.Value != request.PublicDiscovery)
                    throw new InvalidOperationException("A profile publication has an unknown result. Retry the original update or resolve the profile first.");
            }
            else
            {
                var current = await ResolveProfileAsync(AccountId, cancellationToken, relayId).ConfigureAwait(false);
                var basis = current?.Profile;
                if (restored is not null)
                {
                    var local = await database.AccountProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == AccountId, cancellationToken).ConfigureAwait(false);
                    if (local is not null && local.UpdatedAt > restored.UpdatedAt) restored = ProtocolModel.FromJson<AccountProfile>(local.DocumentJson)!;
                    if (signed is { Pending: true } && signed.Revision >= restored.UpdatedAt) restored = ProtocolModel.FromJson<AccountProfile>(signed.DocumentJson)!;
                    if (basis is null || restored.UpdatedAt > basis.UpdatedAt) basis = restored;
                }
                basis ??= new AccountProfile { Account = AccountId, PublicDiscovery = false, UpdatedAt = 0, DeviceSignature = [] };
                request = basis with
                {
                    Nickname = Apply(update.Nickname, basis.Nickname),
                    Avatar = Apply(update.Avatar, basis.Avatar),
                    Bio = Apply(update.Bio, basis.Bio),
                    PublicDiscovery = update.PublicDiscovery.IsSpecified ? update.PublicDiscovery.Value : basis.PublicDiscovery,
                    UpdatedAt = Math.Max(Clock.UtcNow.ToUnixTimeSeconds(), basis.UpdatedAt),
                    DeviceSignature = []
                };
                request = request with { DeviceSignature = [.. await deviceSigner.SignAsync(request.GetSigningInput(Context), cancellationToken).ConfigureAwait(false)] };
                if (request.Validate() is { } violation)
                    throw new ArgumentException(violation.Message, nameof(update));
                if (signed is null)
                {
                    signed = new SignedRequestRecord { Method = "profile.publish", RelayId = relayId, DocumentJson = request.ToJson(), Revision = request.UpdatedAt, Pending = true };
                    database.SignedRequests.Add(signed);
                }
                else
                {
                    signed.RelayId = relayId;
                    signed.DocumentJson = request.ToJson();
                    signed.Revision = request.UpdatedAt;
                    signed.Pending = true;
                }
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            var relayClient = await relayClients.GetAsync(relayId, deviceSigner, cancellationToken).ConfigureAwait(false);
            try
            {
                await relayClient.SendHttpAsync(HttpMethod.Put, "profile.publish", request, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RelayException exception) when (exception.Error.IsDefinitiveRejection())
            {
                signed.Pending = false;
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            await SaveProfileAsync(new ProfileResolveResult { Profile = request, SignerCertificate = certificate }, cancellationToken).ConfigureAwait(false);
            changed = Profile?.ToJson() != request.ToJson();
            Profile = request;
        }
        finally
        {
            _profileGate.Release();
        }
        if (changed)
            ProfileChanged?.Invoke(this, EventArgs.Empty);
        return request;
    }

    static T? Apply<T>(FieldUpdate<T> update, T? current) where T : class => update.IsDeleted ? null : update.IsSpecified ? update.Value : current;

    async Task<ProfileResolveResult?> ResolveProfileAsync(string accountId, CancellationToken cancellationToken, string? relayId = null)
    {
        if (relayId is null)
        {
            var route = await accountManager.GetRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account route could not be found. Establish or recover the account explicitly.");
            relayId = route.RelayId;
        }
        var relayClient = await relayClients.GetAsync(relayId, deviceSigner, cancellationToken).ConfigureAwait(false);
        ProfileResolveResult result;
        try
        {
            result = await relayClient.SendHttpAsync<ProfileResolveResult>(HttpMethod.Get, "profile.resolve", new AccountQuery { Account = accountId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RelayException exception) when (exception.Error.Code == "not_found")
        {
            return null;
        }
        var profile = result.Profile;
        var certificate = result.SignerCertificate;
        if (profile.Account != accountId || certificate.Account != accountId)
            throw new InvalidDataException("The profile or signing certificate belongs to another account.");
        if (!Ed25519.Verify(profile.DeviceSignature.AsSpan(), certificate.SigningPublicKey.AsSpan(), profile.GetSigningInput(Context)))
            throw new CryptographicException("The profile device signature is invalid.");
        return result;
    }

    async Task SaveProfileAsync(ProfileResolveResult result, CancellationToken cancellationToken)
    {
        var profile = result.Profile;
        var json = profile.ToJson();
        await using var database = new MeshlineDbContext(databaseOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = await database.AccountProfiles.SingleOrDefaultAsync(value => value.AccountId == profile.Account, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            if (profile.UpdatedAt < record.UpdatedAt)
                throw new InvalidDataException("The returned profile is older than the known profile.");
            record.DocumentJson = json;
            record.SignerCertificateJson = result.SignerCertificate.ToJson();
            record.UpdatedAt = profile.UpdatedAt;
        }
        else
            database.AccountProfiles.Add(new AccountProfileRecord { AccountId = profile.Account, DocumentJson = json, SignerCertificateJson = result.SignerCertificate.ToJson(), UpdatedAt = profile.UpdatedAt });
        if (profile.Account == AccountId)
        {
            var pending = await database.SignedRequests.SingleOrDefaultAsync(value => value.Method == "profile.publish", cancellationToken).ConfigureAwait(false);
            if (pending is { Pending: true } && (profile.UpdatedAt > pending.Revision || profile.UpdatedAt == pending.Revision && json == pending.DocumentJson))
                pending.Pending = false;
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
