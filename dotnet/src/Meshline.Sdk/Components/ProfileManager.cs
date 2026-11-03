using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
public sealed partial class ProfileManager(ClientOptions options, DatabaseOptions databaseOptions, RelayClientPool relayClients, AccountManager accountManager, IDeviceSigner deviceSigner) : ClientComponent(options)
{
    /// <summary>
    /// Occurs when the current account's locally known profile changes.
    /// </summary>
    public event EventHandler? ProfileChanged;

    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public string AccountId => Options.AccountId;
    /// <summary>
    /// The current account's confirmed profile, or <see langword="null"/> when it is unavailable. Pending local edits are exposed by GetUpdateStateAsync.
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
        await LoadProfileCacheAsync(database, cancellationToken).ConfigureAwait(false);
        await InitializeProfileUpdatesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a verified account profile with demand-triggered background refresh and updates its local cache.
    /// </summary>
    /// <param name="accountId">The target account identifier, or <see langword="null"/> for the current account.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The resolved profile, or <see langword="null"/> when no profile is available.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
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
        return (await ReadProfileAsync(accountId, false, cancellationToken).ConfigureAwait(false))?.Profile;
    }

    /// <summary>
    /// Applies profile field updates, signs the result with the local device, and publishes it.
    /// </summary>
    /// <param name="update">Field assignments and deletions. Omitted fields retain existing values and unfinished edits; later changes to the same field win.</param>
    /// <param name="cancellationToken">Cancels the call without discarding saved changes.</param>
    /// <returns>The signed profile published after applying the updates.</returns>
    /// <exception cref="StateUpdateSupersededException">A newer update superseded this call.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
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
    /// <exception cref="ArgumentException">The required public-discovery field is deleted or a supplied profile field fails validation.</exception>
    public Task<AccountProfile> UpdateProfileAsync(ProfileUpdate update, CancellationToken cancellationToken = default) =>
        UpdateLatestProfileAsync(update, cancellationToken);

    /// <summary>
    /// Republishes the newest available profile for this account, retaining unfinished field changes.
    /// </summary>
    /// <param name="profile">An optional profile snapshot for this account to consider alongside cached state. Among pending and supplied snapshots, the higher UpdatedAt wins; equal timestamps retain the pending snapshot.</param>
    /// <param name="cancellationToken">Cancels the call without discarding saved changes.</param>
    /// <returns>The republished profile, or <see langword="null"/> if no profile was available to publish.</returns>
    /// <exception cref="StateUpdateSupersededException">A newer update superseded this call.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
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
    /// <exception cref="ArgumentException">The supplied profile belongs to another account or fails protocol validation.</exception>
    public async Task<AccountProfile?> PublishProfileAsync(AccountProfile? profile = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (profile is not null && profile.Account != AccountId)
            throw new ArgumentException("The profile must belong to the current account.", nameof(profile));
        if (profile?.Validate() is { } invalid) throw new ArgumentException(invalid.Message, nameof(profile));
        if (profile is null)
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            if (!await database.AccountProfiles.AnyAsync(value => value.AccountId == AccountId, cancellationToken).ConfigureAwait(false)
                && !await database.SignedRequests.AnyAsync(value => value.Method == "profile.publish" && value.Pending, cancellationToken).ConfigureAwait(false))
                return null;
        }
        try
        {
            // Only caller-supplied snapshots become intent. The owning attempt reads
            // current cache and pending work, so retired requests cannot be reintroduced.
            return await Updates.SubmitAsync<AccountProfile>(AccountId,
                profile is null ? new() : new() { ["snapshot"] = (profile with { DeviceSignature = [] }).ToJson() }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ProfilePublicationException or AggregateException)
        {
            ExceptionDispatchInfo.Throw(ProfilePublicationException.Unwrap(error));
            throw;
        }
    }

    internal async Task InitializeAccountProfileAsync(AccountInitialProfile initial, bool createIfMissing, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var callerToken = cancellationToken;
        using var operation = BeginOperation(ref cancellationToken);
        var publication = Interlocked.Read(ref _profilePublication);
        var existing = await ResolveProfileAsync(AccountId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await SaveProfileAsync(existing, cancellationToken, publication).ConfigureAwait(false);
            return;
        }
        if (!createIfMissing) return;
        await using var database = new MeshlineDbContext(databaseOptions);
        if (await database.AccountProfiles.AnyAsync(value => value.AccountId == AccountId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("A previously known profile is unavailable; initial values cannot replace it.");
        var intent = new JsonObject { ["initialize_only"] = true, ["public_discovery"] = initial.PublicDiscovery };
        if (initial.Nickname is not null) intent["nickname"] = initial.Nickname;
        if (initial.Bio is not null) intent["bio"] = initial.Bio;
        if (initial.Avatar is not null) intent["avatar"] = JsonNode.Parse(initial.Avatar.ToJson());
        try
        {
            await Updates.ResumeAsync<AccountProfile>(AccountId, intent, cancellationToken).ConfigureAwait(false);
        }
        catch (ProfilePublicationException exception) when (callerToken.IsCancellationRequested && exception.InnerException is OperationCanceledException)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
        catch (AggregateException exception) when (exception.Flatten().InnerExceptions.Any(value => value is ProfilePublicationException))
        {
            throw new ProfilePublicationException(ProfilePublicationException.Unwrap(exception));
        }
    }

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

    async Task<bool> StoreProfileAsync(ProfileResolveResult result, bool published, CancellationToken cancellationToken)
    {
        var confirmed = false;
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
            var initializeOnly = pending is { Pending: true, IntentJson: not null }
                && JsonNode.Parse(pending.IntentJson)!["initialize_only"]?.GetValue<bool>() == true;
            if (pending is { Pending: true } && (initializeOnly
                || (pending.IntentJson is null || pending.RequestState.IsCurrent())
                    && (profile.UpdatedAt > pending.Revision || profile.UpdatedAt == pending.Revision && json == pending.DocumentJson)))
            {
                // Discovery ends creation-only intent even before a request was prepared.
                // Fence in-flight work in the same transaction; its late result must not
                // restore defaults. An owning publication completes its own generation.
                if (initializeOnly && !published)
                {
                    pending.Generation = checked(pending.Generation + 1);
                    pending.RequestState = pending.RequestState.Supersede();
                }
                pending.Pending = false;
                pending.IntentJson = null;
                pending.NextAttemptAt = null;
                pending.LastErrorJson = null;
                confirmed = true;
            }
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return confirmed;
    }
}
