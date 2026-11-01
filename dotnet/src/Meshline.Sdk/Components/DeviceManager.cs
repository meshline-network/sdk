using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Meshline.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Meshline.Components;

/// <summary>
/// Manages the local device's protected keys, certificates, published authorization, and device signing.
/// </summary>
/// <remarks>
/// Callers must not run device creation, renewal, removal, or state publication concurrently with
/// <see cref="MeshlineClient.EstablishAccountAsync"/> on the same client, including from signing or event callbacks.
/// Await the current operation's completion, failure, or cancellation before starting the other operation.
/// </remarks>
/// <param name="options">The network and account configuration for this component.</param>
/// <param name="databaseOptions">The SQLite database configuration; create its parent directory and apply migrations before initialization.</param>
/// <param name="relayClients">The shared relay pool. The application owns it and must dispose it after all dependent components.</param>
/// <param name="accountManager">The account component sharing this network, account, database, and relay pool.</param>
/// <param name="accountSigner">The application-owned account signer required for account-authorized operations, or <see langword="null"/> when those operations are not needed.</param>
/// <param name="secretProtector">The application-owned secret protector, required for operations that persist or restore protected key or message material.</param>
/// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null. The <paramref name="options"/> argument is null.</exception>
/// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
/// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
public sealed partial class DeviceManager(ClientOptions options, DatabaseOptions databaseOptions, RelayClientPool relayClients, AccountManager accountManager, IAccountSigner? accountSigner = null, ISecretProtector? secretProtector = null) : ClientComponent(options), IDeviceSigner
{
    /// <summary>
    /// Occurs when the current account's known device authorization state changes.
    /// </summary>
    public event EventHandler? DeviceStateChanged;
    /// <summary>
    /// Occurs when a local device certificate is created or renewed.
    /// </summary>
    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private byte[]? _signingKey;
    private byte[]? _encryptionKey;
    readonly Lock _homeGate = new();
    RelayClient? _homeRelayClient;
    Channel<bool>? _refreshRequests;
    Task _homeRefresh = Task.CompletedTask;
    readonly ConcurrentDictionary<string, long> _requiredDeviceRevisions = new(StringComparer.Ordinal);

    /// <summary>
    /// The shared relay pool used by this device manager.
    /// </summary>
    public RelayClientPool RelayClients => relayClients;
    /// <summary>
    /// The local device certificate, or <see langword="null"/> when no local device has been loaded or created.
    /// </summary>
    public DeviceCertificate? Local { get; private set; }
    /// <summary>
    /// The currently known account device state, or <see langword="null"/> when it is unavailable.
    /// </summary>
    public AccountDeviceState? DeviceState { get; private set; }

    DeviceCertificate IDeviceSigner.Certificate
    {
        get
        {
            EnsureInitialized();
            return Local ?? throw new InvalidOperationException("No local device has been created.");
        }
    }

    /// <summary>
    /// Creates local Ed25519 and X25519 keys and an account-authorized device certificate, then protects and stores them.
    /// </summary>
    /// <param name="validity">The validity duration, from one second through 720 days.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The newly stored local device certificate.</returns>
    /// <remarks>
    /// Requires an account signer, a secret protector, and a database without a local device or unfinished device preparation/publication. This overload omits the push gateway declaration. The returned certificate is stored locally; publish device state before treating the device as authorized by the relay.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. An account signer or required secret protector is unavailable, a local device already exists, or device preparation/publication is unfinished.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails. The account signer produces a certificate that fails identity or signature validation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested certificate validity is outside one second through 720 days.</exception>
    public Task<DeviceCertificate> CreateDeviceAsync(TimeSpan validity, CancellationToken cancellationToken = default) =>
        CreateDeviceAsync(new DeviceCreationOptions { Validity = validity }, cancellationToken);

    /// <summary>
    /// Creates and stores a new local device with the supplied certificate lifetime and optional push gateway declaration.
    /// </summary>
    /// <param name="options">The certificate lifetime and optional push gateway declaration.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The newly stored local device certificate.</returns>
    /// <remarks>
    /// Requires an account signer, a secret protector, and no local device or unfinished device preparation/publication.
    /// Publish device state before treating the device as authorized by the relay.
    /// </remarks>
    /// <inheritdoc cref="CreateDeviceAsync(TimeSpan, CancellationToken)" path="/exception"/>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The push gateway is not a valid protocol HTTPS URL.</exception>
    public async Task<DeviceCertificate> CreateDeviceAsync(DeviceCreationOptions options, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(options);
        var validity = options.Validity;
        var pushGateway = options.PushGateway;
        ValidateValidity(validity);
        if (pushGateway is not null && PushGatewayValidator.Validate(pushGateway) is { } violation)
            throw new ArgumentException(violation.Message, nameof(options));
        using var _ = BeginOperation(ref cancellationToken);
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        return await CreateDeviceCoreAsync(validity, pushGateway, cancellationToken).ConfigureAwait(false);
    }

    async Task<DeviceCertificate> CreateDeviceCoreAsync(TimeSpan validity, string? pushGateway, CancellationToken cancellationToken)
    {
        ValidateValidity(validity);
        var protector = secretProtector ?? throw new InvalidOperationException("A secret protector is required to create a local device.");
        if (accountSigner is null)
            throw new InvalidOperationException("An account signer is required to authorize a device.");
        DeviceCertificate certificate;
        await using var database = new MeshlineDbContext(databaseOptions);
        await RequireCompletedDeviceMutationAsync(database, cancellationToken).ConfigureAwait(false);
        var binding = await database.Bindings.SingleAsync(cancellationToken).ConfigureAwait(false);
        if (binding.DeviceId is not null || await database.LocalDevices.AnyAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A local device already exists in this database.");
        byte[]? signingKey = null;
        byte[]? encryptionKey = null;
        try
        {
            signingKey = RandomNumberGenerator.GetBytes(32);
            encryptionKey = RandomNumberGenerator.GetBytes(32);
            var encryptionPublicKey = new byte[32];
            X25519.GeneratePublicKey(encryptionKey.AsSpan(), encryptionPublicKey.AsSpan());
            certificate = await IssueCertificateAsync(signingKey, [.. encryptionPublicKey], validity, pushGateway, cancellationToken).ConfigureAwait(false);
            var deviceId = certificate.GetDeviceId(Context);
            var protectedSigningKey = await protector.ProtectAsync(signingKey, KeyPurpose(deviceId, "signing"), cancellationToken).ConfigureAwait(false);
            var protectedEncryptionKey = await protector.ProtectAsync(encryptionKey, KeyPurpose(deviceId, "encryption"), cancellationToken).ConfigureAwait(false);
            database.LocalDevices.Add(new LocalDeviceRecord
            {
                DeviceId = deviceId,
                CertificateJson = certificate.ToJson(),
                ProtectedSigningKey = protectedSigningKey[..],
                ProtectedEncryptionKey = protectedEncryptionKey[..]
            });
            binding.DeviceId = deviceId;
            await CommitLocalDeviceAsync(database, certificate, signingKey, encryptionKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (signingKey is not null)
                CryptographicOperations.ZeroMemory(signingKey);
            if (encryptionKey is not null)
                CryptographicOperations.ZeroMemory(encryptionKey);
        }
        DeviceChanged?.Invoke(this, new(certificate.GetDeviceId(Context)));
        return certificate;
    }

    /// <summary>
    /// Issues and stores a renewed certificate for the existing local device without replacing its keys.
    /// </summary>
    /// <param name="validity">The validity duration, from one second through 720 days.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The renewed local device certificate with the same device keys.</returns>
    /// <remarks>
    /// The renewed certificate retains the device identity and push gateway declaration. Complete any unfinished device preparation or publication before renewing. Publish device state to make the renewed authorization authoritative at the relay.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. An account signer or required secret protector is unavailable, no local device has been created, or device preparation/publication is unfinished.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails. The account signer produces a certificate that fails identity or signature validation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested certificate validity is outside one second through 720 days.</exception>
    /// <exception cref="InvalidDataException">The stored local device certificate is JSON null.</exception>
    public Task<DeviceCertificate> RenewDeviceAsync(TimeSpan validity, CancellationToken cancellationToken = default) =>
        RenewDeviceAsync(new DeviceRenewalOptions { Validity = validity }, cancellationToken);

    /// <summary>
    /// Renews and stores the local certificate with an optional push gateway change, retaining the device keys and identity.
    /// </summary>
    /// <param name="options">The certificate lifetime and gateway update. See <see cref="DeviceRenewalOptions.PushGateway"/> for retain, set and remove semantics.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The renewed local device certificate with the same device keys.</returns>
    /// <remarks>
    /// Requires account signing and no unfinished device preparation or publication. Renewal stores the certificate
    /// and retires device sessions; publish device state to make the new authorization authoritative at the relay.
    /// </remarks>
    /// <inheritdoc cref="RenewDeviceAsync(TimeSpan, CancellationToken)" path="/exception"/>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The assigned gateway is null or is not a valid protocol HTTPS URL.</exception>
    public async Task<DeviceCertificate> RenewDeviceAsync(DeviceRenewalOptions options, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(options);
        var validity = options.Validity;
        var pushGateway = options.PushGateway;
        ValidateValidity(validity);
        if (pushGateway.IsSpecified)
        {
            if (pushGateway.Value is null)
                throw new ArgumentException("Use FieldUpdate<string>.Delete to remove the push gateway declaration.", nameof(options));
            if (PushGatewayValidator.Validate(pushGateway.Value) is { } violation)
                throw new ArgumentException(violation.Message, nameof(options));
        }
        using var _ = BeginOperation(ref cancellationToken);
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        return await RenewDeviceCoreAsync(validity, pushGateway, cancellationToken).ConfigureAwait(false);
    }

    async Task<DeviceCertificate> RenewDeviceCoreAsync(TimeSpan validity, FieldUpdate<string> pushGateway,
        CancellationToken cancellationToken)
    {
        ValidateValidity(validity);
        if (accountSigner is null)
            throw new InvalidOperationException("An account signer is required to renew a device.");
        DeviceCertificate certificate;
        await using var database = new MeshlineDbContext(databaseOptions);
        await RequireCompletedDeviceMutationAsync(database, cancellationToken, allowPendingPublication: true).ConfigureAwait(false);
        var local = await database.LocalDevices.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No local device has been created.");
        var previous = ReadLocalCertificate(local);
        var signingKey = await GetKeyCopyAsync(encryption: false, cancellationToken).ConfigureAwait(false);
        try
        {
            certificate = await IssueCertificateAsync(signingKey, previous.EncryptionPublicKey, validity,
                pushGateway.IsDeleted ? null : pushGateway.IsSpecified ? pushGateway.Value : previous.PushGateway, cancellationToken).ConfigureAwait(false);
            local.CertificateJson = certificate.ToJson();
            await CommitLocalDeviceAsync(database, certificate, null, null, cancellationToken).ConfigureAwait(false);
            await relayClients.RebindDeviceAsync(local.DeviceId, certificate).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(signingKey); }
        DeviceChanged?.Invoke(this, new(certificate.GetDeviceId(Context)));
        return certificate;
    }

    /// <summary>
    /// Publishes a complete device state with the selected device removed after checking contact-grant continuity.
    /// </summary>
    /// <param name="deviceId">The canonical device identifier.</param>
    /// <param name="cancellationToken">Cancels the call without discarding saved changes.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>Pending removals accumulate by device ID.</remarks>
    /// <exception cref="StateUpdateSupersededException">A newer update superseded this call.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. Published device state or retained contact history is unavailable, removal would invalidate a contact grant without accepted replacement signatures, or the relay only stages the removal.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The <paramref name="deviceId"/> argument is not a canonical device identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The next device-state revision exceeds the protocol safe-integer limit.</exception>
    public Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken = default) =>
        RemoveLatestDeviceAsync(deviceId, cancellationToken);
    /// <summary>
    /// Finds a device certificate in the currently known account device state.
    /// </summary>
    /// <param name="deviceId">The canonical device identifier.</param>
    /// <returns>The registered certificate, or <see langword="null"/> when the state or device is unknown.</returns>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="ArgumentException">The <paramref name="deviceId"/> argument is not a canonical device identifier.</exception>
    public DeviceCertificate? GetCertificate(string deviceId)
    {
        EnsureInitialized();
        if (Identifiers.ValidateDeviceId(deviceId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(deviceId));
        return DeviceState?.Certificates.FirstOrDefault(entry => entry.GetDeviceId(Context) == deviceId);
    }

    /// <summary>
    /// Evaluates registration and certificate validity in the currently known account device state.
    /// </summary>
    /// <param name="deviceId">The canonical device identifier.</param>
    /// <returns>The device's authorization state according to locally known registration and the current time.</returns>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="ArgumentException">The <paramref name="deviceId"/> argument is not a canonical device identifier.</exception>
    public DeviceAuthorizationState GetAuthorizationState(string deviceId)
    {
        EnsureInitialized();
        if (Identifiers.ValidateDeviceId(deviceId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(deviceId));
        var state = DeviceState;
        if (state is null)
            return DeviceAuthorizationState.Unknown;
        var certificate = state.Certificates.FirstOrDefault(entry => entry.GetDeviceId(Context) == deviceId);
        if (certificate is null)
            return DeviceAuthorizationState.NotRegistered;
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        return now < certificate.NotBefore ? DeviceAuthorizationState.NotYetValid
            : now >= certificate.ExpiresAt ? DeviceAuthorizationState.Expired
            : DeviceAuthorizationState.Authorized;
    }

    /// <summary>
    /// Returns verified cached device state, starting a demand-triggered background refresh when due.
    /// </summary>
    /// <remarks>
    /// Reads trigger refreshes one hour after success or one minute after failure. Missing or unusable state requires a shared fetch;
    /// caller cancellation only ends that caller's wait. State signatures and revisions are verified before caching, and authorization is checked on use.
    /// Use <see cref="RefreshDeviceStateAsync(string, CancellationToken)"/> to await a refresh.
    /// </remarks>
    /// <param name="accountId">The target account identifier, or <see langword="null"/> for the current account.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The verified device state, or <see langword="null"/> when unavailable.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The home route, local device, or account signer needed to authenticate the query is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The target account or relay identifier is invalid, or supplied contact evidence does not authorize a query for this account.</exception>
    /// <exception cref="NotSupportedException">An account identifier in the query uses an unsupported namespace.</exception>
    public async Task<AccountDeviceState?> GetDeviceStateAsync(string? accountId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await ResolveDeviceStateAsync(accountId ?? Options.AccountId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns verified cached device state, starting a demand-triggered background refresh when due.
    /// </summary>
    /// <param name="grant">A contact grant authorizing this account to access the grantor's device state.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The verified device state, or <see langword="null"/> when unavailable.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The home route, local device, or account signer needed to authenticate the query is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails. The grant has no valid signature from a currently authorized device.</exception>
    /// <exception cref="ArgumentException">The target account or relay identifier is invalid, or supplied contact evidence does not authorize a query for this account.</exception>
    /// <exception cref="NotSupportedException">An account identifier in the query uses an unsupported namespace.</exception>
    public async Task<AccountDeviceState?> GetDeviceStateAsync(ContactGrant grant, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (grant.Validate() is { } violation)
            throw new ArgumentException(violation.Message, nameof(grant));
        if (grant.Grantee != Options.AccountId)
            throw new ArgumentException("The contact grant must authorize this account.", nameof(grant));
        return await ResolveDeviceStateAsync(grant.Grantor, grant, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns verified cached device state, starting a demand-triggered background refresh when due.
    /// </summary>
    /// <param name="invite">The signed invitation authorizing the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The verified device state, or <see langword="null"/> when unavailable.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The home route, local device, or account signer needed to authenticate the query is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The invitation signature is invalid, a local signing key fails validation, or the cryptographic provider fails signature verification.</exception>
    /// <exception cref="ArgumentException">The target account or relay identifier is invalid, or supplied contact evidence does not authorize a query for this account.</exception>
    /// <exception cref="NotSupportedException">An account identifier in the query uses an unsupported namespace.</exception>
    public async Task<AccountDeviceState?> GetDeviceStateAsync(ContactInvite invite, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (invite.Validate() is { } violation)
            throw new ArgumentException(violation.Message, nameof(invite));
        return await ResolveDeviceStateAsync(invite.Inviter, invite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the current account's device state directly from the specified relay.
    /// </summary>
    /// <remarks>
    /// While running, queries use device authentication only. A stopped manager may use account authentication when its local device authorization is unavailable. Device-authenticated reads retry an expired session with the same identity but never escalate a rejection into account signing; use explicit account establishment for reauthorization.
    /// </remarks>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The current account's verified device state, or <see langword="null"/> when unavailable at that relay.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The home route, local device, or account signer needed to authenticate the query is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The target account or relay identifier is invalid, or supplied contact evidence does not authorize a query for this account.</exception>
    /// <exception cref="NotSupportedException">An account identifier in the query uses an unsupported namespace.</exception>
    public async Task<AccountDeviceState?> GetOwnDeviceStateAsync(string relayId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await ResolveDeviceStateAsync(Options.AccountId, null, cancellationToken, relayId, mode: StateReadMode.RefreshAfterCurrent).ConfigureAwait(false);
    }
    /// <summary>
    /// Signs and publishes a complete device authorization state and reports acceptance or staging.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="certificates">The complete certificate list, replacing unfinished removals, or <see langword="null"/> to preserve known devices and refresh the local certificate.</param>
    /// <param name="options">Optional prior-state, recovery, and revision settings.</param>
    /// <param name="cancellationToken">Cancels the call without discarding saved changes.</param>
    /// <returns>The submitted state and the relay's authoritative-acceptance or temporary-staging result.</returns>
    /// <remarks>Unavailable prior state or missing local authorization requires explicit recovery.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="StateUpdateSupersededException">A newer update superseded this call.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The account signer or complete prior device state is unavailable, or the local device is unauthorized outside recovery.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">The relay response or returned device state is inconsistent, including an expired staging interval.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The prior state belongs to another account or is invalid, or the complete certificate list fails protocol validation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The selected device-state revision is not a newer nonnegative safe integer. A relay-provided staging timestamp is outside the supported DateTimeOffset range.</exception>
    public Task<DeviceStatePublishResult> PublishDeviceStateAsync(string relayId, IReadOnlyList<DeviceCertificate>? certificates = null, DeviceStatePublishOptions? options = null, CancellationToken cancellationToken = default) =>
        UpdateDevicesAsync(relayId, certificates, options, cancellationToken);

    async Task<DeviceStatePublishResult> PublishDeviceStateCoreAsync(string relayId, IReadOnlyList<DeviceCertificate>? certificates,
        DeviceStatePublishOptions? options, CancellationToken cancellationToken, StateUpdateAttempt? attempt = null,
        AccountDeviceState? continuityBasis = null, IReadOnlyList<string>? removals = null)
    {
        if (options?.PreviousState is { } supplied && (supplied.Account != Options.AccountId || supplied.Validate(Context) is not null))
            throw new ArgumentException("The previous device state must be valid and belong to this account.", nameof(options));
        if (accountSigner is null)
            throw new InvalidOperationException("An account signer is required to publish device state.");
        var preserveDevices = certificates is null;
        var refreshStaging = preserveDevices && options?.IsRecovery != true && options?.Revision is null && attempt?.RequestJson is null;
        await using var database = new MeshlineDbContext(databaseOptions);
        var signed = await database.SignedRequests.SingleOrDefaultAsync(value => value.Method == "device.state.publish", cancellationToken).ConfigureAwait(false);
        var known = await database.DeviceStates.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == Options.AccountId, cancellationToken).ConfigureAwait(false);
        var knownRevision = Math.Max(Math.Max(known?.Revision ?? -1, signed?.Revision ?? -1), options?.PreviousState?.Revision ?? -1);
        if (preserveDevices)
        {
            var previous = options?.PreviousState;
            if (known is not null && (previous is null || known.Revision > previous.Revision)) previous = ProtocolModel.FromJson<AccountDeviceState>(known.DocumentJson)!;
            if (attempt is null && refreshStaging && signed is { Pending: true, DocumentJson: not null, RequestState: PublicationRequestState.Prepared or PublicationRequestState.Superseded } && signed.RelayId != relayId)
                throw new InvalidOperationException("Resolve the pending device state publication before transferring it to another relay.");
            if (signed is { DocumentJson: not null } && signed.RelayId == relayId && (previous is null || signed.Revision > previous.Revision))
                previous = ProtocolModel.FromJson<AccountDeviceState>(signed.DocumentJson)!;
            if (previous is null && options?.IsRecovery != true)
                throw new InvalidOperationException("The complete device state is unavailable. Supply the complete certificate list or recover the account explicitly.");
            certificates = previous?.Certificates.ToArray() ?? [];
            if (Local is { } local)
            {
                var deviceId = local.GetDeviceId(Context);
                var registered = certificates.Any(value => value.GetDeviceId(Context) == deviceId);
                if (!registered && options?.IsRecovery != true)
                    throw new InvalidOperationException("The local device is absent from the complete device state. Authorize it or recover the account explicitly.");
                certificates = registered ? certificates.Select(value => value.GetDeviceId(Context) == deviceId ? local : value).ToArray() : [.. certificates, local];
            }
        }
        var pendingState = signed is { Pending: true, DocumentJson: not null, RequestState: PublicationRequestState.Prepared or PublicationRequestState.Superseded } ? ProtocolModel.FromJson<AccountDeviceState>(signed.DocumentJson)! : null;
        var retryPending = !refreshStaging && pendingState is not null && signed!.RelayId == relayId && (options?.Revision is null || options.Revision == signed.Revision)
            && MatchesCertificates(pendingState, certificates!);
        long revision;
        if (retryPending) revision = pendingState!.Revision;
        else
        {
            if (attempt is null && pendingState is not null && !refreshStaging && options?.IsRecovery != true && options?.Revision is null)
                throw new InvalidOperationException("Device state publication has an unknown result. Retry the original certificates or resolve the current state before publishing a change.");
            var requestedRevision = options?.Revision;
            if (requestedRevision is null && options?.IsRecovery == true)
                requestedRevision = Math.Max(knownRevision + 1, Clock.UtcNow.ToUnixTimeMilliseconds());
            revision = AccountDeviceState.GetNextRevision(requestedRevision, knownRevision);
        }
        async Task CheckContinuityAsync()
        {
            if (continuityBasis is null || removals is null) return;
            foreach (var id in removals)
            {
                var issues = await CheckRemovalContinuityAsync(database, continuityBasis, id, certificates!, cancellationToken).ConfigureAwait(false);
                if (issues.Count != 0) throw new InvalidOperationException($"Device removal lacks contact-grant continuity evidence: {string.Join(", ", issues.Select(value => value.Code).Distinct())}.");
            }
        }
        await CheckContinuityAsync().ConfigureAwait(false);
        var request = await PrepareDeviceStatePublicationAsync(signed, relayId, certificates!, revision, cancellationToken).ConfigureAwait(false);
        async Task SaveAsync()
        {
            await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false);
            await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
            if (continuityBasis is not null)
            {
                var current = await database.DeviceStates.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == Options.AccountId, cancellationToken).ConfigureAwait(false);
                if (current is not null && (current.Revision > continuityBasis.Revision || current.Revision == continuityBasis.Revision && current.DocumentJson != continuityBasis.ToJson()))
                    throw new DeviceStatePublicationException(DeviceStatePublicationFailure.StateChanged, "Device state changed while signing; rebuild the latest removal against current state.");
            }
            await CheckContinuityAsync().ConfigureAwait(false);
            signed = await SaveDeviceStatePublicationAsync(database, signed, relayId, request, cancellationToken, generation: attempt?.Generation).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (attempt is null) await SaveAsync().ConfigureAwait(false);
        else
        {
            await attempt.CurrentAsync(SaveAsync, cancellationToken).ConfigureAwait(false);
            attempt.RecordRequest(request.ToJson());
        }
        return await SubmitDeviceStatePublicationAsync(database, signed!, request, cancellationToken, attempt: attempt).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs the supplied bytes using the device Ed25519 signing key.
    /// </summary>
    /// <param name="data">The exact bytes to sign or verify.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The signature over the supplied input bytes.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or the secret protector required to load its keys is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="InvalidDataException">The stored local device certificate is JSON null.</exception>
    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        var key = await GetKeyCopyAsync(encryption: false, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = new byte[Ed25519.SignatureSize];
            Ed25519.Sign(key.AsSpan(), data.Span, signature.AsSpan());
            return signature;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Performs X25519 key agreement with the local device's encryption private key.
    /// </summary>
    /// <param name="peerPublicKey">The peer's 32-byte X25519 public key.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A new 32-byte buffer containing the raw shared secret.</returns>
    /// <remarks>
    /// The returned bytes are raw key-agreement material. Apply the protocol's required key derivation before using them as a cryptographic key and clear sensitive buffers after use.
    /// </remarks>
    /// <exception cref="ArgumentException">The peer key is not 32 bytes or produces an all-zero shared secret.</exception>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or the secret protector required to load its keys is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="InvalidDataException">The stored local device certificate is JSON null.</exception>
    public async Task<byte[]> DeriveSharedSecretAsync(ReadOnlyMemory<byte> peerPublicKey, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        if (peerPublicKey.Length != X25519.PointSize)
            throw new ArgumentException("An X25519 public key must contain 32 bytes.", nameof(peerPublicKey));
        var key = await GetKeyCopyAsync(encryption: true, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sharedSecret = new byte[32];
            if (!X25519.CalculateAgreement(key.AsSpan(), peerPublicKey.Span, sharedSecret.AsSpan()))
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
                throw new ArgumentException("The X25519 public key produces an all-zero shared secret.", nameof(peerPublicKey));
            }
            return sharedSecret;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">The database is bound to another network or account, or a required dependency has not been initialized.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="InvalidDataException">The persisted device binding, local certificate, or stored account device state is missing or inconsistent.</exception>
    protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        var binding = await EnsureDatabaseBindingAsync(database, cancellationToken).ConfigureAwait(false);
        var local = await database.LocalDevices.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        DeviceCertificate? certificate = null;
        if (local is not null)
        {
            certificate = ReadLocalCertificate(local);
            if (certificate.Account != Options.AccountId || local.DeviceId != certificate.GetDeviceId(Context) || binding.DeviceId != local.DeviceId)
                throw new InvalidDataException("The local device does not match the database binding.");
        }
        // Diagnose a retained binding without keys during explicit establishment.
        // Initialization must keep readable history available and never recreate keys.
        var stateRecord = await database.DeviceStates.AsNoTracking().SingleOrDefaultAsync(entry => entry.AccountId == Options.AccountId, cancellationToken).ConfigureAwait(false);
        AccountDeviceState? state = null;
        if (stateRecord is not null)
            state = ProtocolModel.FromJson<AccountDeviceState>(stateRecord.DocumentJson)
                ?? throw new InvalidDataException("The stored device state is null.");
        Local = certificate;
        DeviceState = state;
        foreach (var row in await database.DeviceStates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            var cached = ProtocolModel.FromJson<AccountDeviceState>(row.DocumentJson)!;
            if (cached.Account != row.AccountId || cached.Revision != row.Revision || cached.Validate(Context) is not null)
                throw new InvalidDataException("The persisted device state is invalid.");
            CacheDeviceState(cached);
        }
        await InitializeDeviceUpdatesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">The local device, home route, or published authorization required by startup is unavailable or invalid.</exception>
    /// <exception cref="ObjectDisposedException">A dependency or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var certificate = Local ?? throw new InvalidOperationException("Create and authorize the local device before starting it.");
        var state = await GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account has no published device state.");
        if (state.ValidateDeviceAuthorization(certificate.GetDeviceId(Context), Context) is { } violation)
            throw new InvalidOperationException(violation.Message ?? "The local device is not authorized.");
        _refreshRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var relayId = await GetHomeRelayIdAsync(cancellationToken).ConfigureAwait(false);
        await UseHomeRelayAsync(relayId, cancellationToken).ConfigureAwait(false);
        _homeRefresh = RefreshHomeAsync(RuntimeCancellationToken);
        accountManager.AccountChanged += OnHomeRouteChanged;
        relayClients.RelayChanged += OnHomeRelayChanged;
        _refreshRequests.Writer.TryWrite(true);
        cancellationToken.ThrowIfCancellationRequested();
        Updates.Start(RuntimeCancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task OnStopAsync()
    {
        accountManager.AccountChanged -= OnHomeRouteChanged;
        relayClients.RelayChanged -= OnHomeRelayChanged;
        List<Exception> failures = [];
        try { if (_updates is { } updates) await updates.StopAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { await Task.WhenAll(_homeRefresh, StopStateQueriesAsync()).ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        _homeRefresh = Task.CompletedTask;
        _refreshRequests = null;
        if (_homeRelayClient is not null)
        {
            SetHomeRelayClient(null);
            // Device sessions own notification/reconnect and renewal tasks independently
            // of the managers. Drain them before reporting the runtime stopped.
            // Retain account sessions and verified relay metadata for the next start.
            try { await relayClients.InvalidateDeviceAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    /// <inheritdoc/>
    /// <exception cref="AggregateException">A cancellation callback throws while dependent component or relay lifetimes are canceled.</exception>
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_updates is { } updates) await updates.DrainAsync().ConfigureAwait(false);
        await StopStateQueriesAsync(dispose: true).ConfigureAwait(false);
        if (Local is not null)
            await relayClients.InvalidateDeviceAsync().ConfigureAwait(false);
        ClearCachedKeys();
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    // Serialize standalone device mutations. Establishment is serialized by the
    // client's account gate and must not overlap caller-initiated device mutations.
    // Key signing and state refresh remain available while an account signer is pending.
    async Task<IDisposable> EnterMutationAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MutationLease(_mutationGate);
    }

    sealed class MutationLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    async Task RequireCompletedDeviceMutationAsync(MeshlineDbContext database, CancellationToken cancellationToken, bool allowPendingPublication = false)
    {
        // Initial authorization remains a distinct durable workflow. Only an already
        // confirmed local identity may be renewed while a state replacement is pending.
        allowPendingPublication &= Local is not null && DeviceState?.Certificates.Any(value => value.GetDeviceId(Context) == Local.GetDeviceId(Context)) == true;
        if (allowPendingPublication && await database.AccountEstablishments.AnyAsync(cancellationToken).ConfigureAwait(false)) allowPendingPublication = false;
        if (await database.DevicePreparations.AnyAsync(value => value.OperationId != null, cancellationToken).ConfigureAwait(false)
            || !allowPendingPublication && await database.SignedRequests.AnyAsync(value => value.Method == "device.state.publish" && value.Pending, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Complete the pending device preparation or publication before creating or renewing a certificate.");
    }

    async Task<DeviceCertificate> IssueCertificateAsync(byte[] signingKey, ImmutableArray<byte> encryptionPublicKey, TimeSpan validity,
        string? pushGateway, CancellationToken cancellationToken)
    {
        var signer = accountSigner ?? throw new InvalidOperationException("An account signer is required to authorize a device.");
        var signingPublicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(signingKey.AsSpan(), signingPublicKey.AsSpan());
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        var certificate = new DeviceCertificate
        {
            Account = Options.AccountId,
            AccountPublicKey = signer.PublicKey,
            SigningPublicKey = [.. signingPublicKey],
            EncryptionPublicKey = encryptionPublicKey,
            NotBefore = now,
            ExpiresAt = checked(now + (long)validity.TotalSeconds),
            PushGateway = pushGateway,
            DeviceSignature = [],
            AccountSignature = []
        };
        var signature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(signingKey.AsSpan(), certificate.GetDeviceSigningInput(Context), signature.AsSpan());
        certificate = certificate with { DeviceSignature = [.. signature] };
        var accountSignature = await signer.SignAsync(certificate.GetAccountSigningInput(Context), cancellationToken).ConfigureAwait(false);
        certificate = certificate with { AccountSignature = [.. accountSignature] };
        if (certificate.Validate(Context) is { } violation)
            throw new CryptographicException(violation.Message ?? "The account signer produced an invalid device certificate.");
        return certificate;
    }

    static DeviceCertificate ReadLocalCertificate(LocalDeviceRecord local) =>
        ProtocolModel.FromJson<DeviceCertificate>(local.CertificateJson)
        ?? throw new InvalidDataException("The stored local certificate is null.");

    static void ValidateValidity(TimeSpan validity)
    {
        if (validity < TimeSpan.FromSeconds(1) || validity > TimeSpan.FromDays(720))
            throw new ArgumentOutOfRangeException(nameof(validity), "Certificate validity must be between one second and 720 days.");
    }

    async Task<byte[]> GetKeyCopyAsync(bool encryption, CancellationToken cancellationToken)
    {
        while (true)
        {
            LocalDeviceRecord local;
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if ((encryption ? _encryptionKey : _signingKey) is { } cachedKey) return cachedKey.ToArray();
                await using var database = new MeshlineDbContext(databaseOptions);
                local = await database.LocalDevices.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No local device has been created.");
            }
            finally { _deviceGate.Release(); }

            // An external key store can ignore cancellation. Do not hold the device
            // gate while loading; the calling operation still owns and drains it.
            byte[]? loaded = await ReadKeyAsync(local, ReadLocalCertificate(local), encryption, cancellationToken).ConfigureAwait(false);
            try
            {
                await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (Local?.GetDeviceId(Context) != local.DeviceId) continue;
                    var cached = encryption ? _encryptionKey : _signingKey;
                    if (cached is null)
                    {
                        cached = loaded;
                        if (encryption) _encryptionKey = cached;
                        else _signingKey = cached;
                        loaded = null;
                    }
                    // Callers own a copy, so replacing or clearing the cache cannot
                    // erase a key in use by a concurrent cryptographic operation.
                    return cached.ToArray();
                }
                finally { _deviceGate.Release(); }
            }
            finally { if (loaded is not null) CryptographicOperations.ZeroMemory(loaded); }
        }
    }

    async Task<byte[]> ReadKeyAsync(LocalDeviceRecord local, DeviceCertificate certificate, bool encryption, CancellationToken cancellationToken)
    {
        var protector = secretProtector ?? throw new InvalidOperationException("A secret protector is required to access local device keys.");
        var key = await protector.UnprotectAsync(encryption ? local.ProtectedEncryptionKey : local.ProtectedSigningKey,
            KeyPurpose(local.DeviceId, encryption ? "encryption" : "signing"), cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.Length != 32)
                throw new CryptographicException("The unprotected device key must contain 32 bytes.");
            var publicKey = new byte[32];
            if (encryption)
                X25519.GeneratePublicKey(key.AsSpan(), publicKey.AsSpan());
            else
                Ed25519.GeneratePublicKey(key.AsSpan(), publicKey.AsSpan());
            if (!CryptographicOperations.FixedTimeEquals(publicKey, (encryption ? certificate.EncryptionPublicKey : certificate.SigningPublicKey).AsSpan()))
                throw new CryptographicException("The unprotected key does not match the local device certificate.");
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    string KeyPurpose(string deviceId, string kind) => $"Meshline/device-{kind}/v1/{Context}/{Options.AccountId}/{deviceId}";

    void ClearCachedKeys()
    {
        if (_signingKey is not null)
        {
            CryptographicOperations.ZeroMemory(_signingKey);
            _signingKey = null;
        }
        if (_encryptionKey is not null)
        {
            CryptographicOperations.ZeroMemory(_encryptionKey);
            _encryptionKey = null;
        }
    }

    async Task<string> GetHomeRelayIdAsync(CancellationToken cancellationToken)
    {
        var route = await accountManager.GetRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account route could not be found. Establish or recover the account explicitly.");
        return route.RelayId;
    }

    async Task<AccountDeviceState?> ResolveDeviceStateAsync(string accountId, TypedProtocolModel? authorization, CancellationToken cancellationToken,
        string? relayId = null, StateReadMode mode = StateReadMode.Cached)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        if (AccountAdapter.ValidateAccountId(accountId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(accountId));
        if (authorization is not null && accountId == Options.AccountId)
            throw new ArgumentException("A signed device state query cannot target the current account.", nameof(accountId));
        await LoadStoredStateAsync(accountId, cancellationToken).ConfigureAwait(false);
        relayId ??= await GetHomeRelayIdAsync(cancellationToken).ConfigureAwait(false);
        var allowAccount = mode != StateReadMode.Cached && LifecycleState == ComponentState.Stopped;
        var useAccount = allowAccount && accountId == Options.AccountId && accountSigner is not null
            && (Local is null || DeviceState?.ValidateDeviceAuthorization(Local.GetDeviceId(Context), Context) is not null || DeviceState is null);
        if (authorization is ContactGrant grant && (grant.Validate() is not null || grant.Grantor != accountId || grant.Grantee != Options.AccountId)
            || authorization is ContactInvite invite && (invite.Validate() is not null || invite.Inviter != accountId))
            throw new ArgumentException("The contact evidence does not authorize this query.", nameof(authorization));
        return await ReadDeviceStateAsync(accountId, authorization, relayId, useAccount, mode, cancellationToken).ConfigureAwait(false);
    }

    async Task<AccountDeviceState?> ResolveDeviceStateCoreAsync(string accountId, TypedProtocolModel? authorization, string relayId, bool useAccount, CancellationToken cancellationToken,
        bool allowAccountFallback = true)
    {
        ProtocolModel parameters = new AccountQuery { Account = accountId };
        if (authorization is not null)
        {
            var request = new SignedDeviceStateQuery
            {
                Account = accountId,
                Authorization = authorization,
                SignerCertificate = Local ?? throw new InvalidOperationException("No local device has been created."),
                CreatedAt = Clock.UtcNow.ToUnixTimeSeconds(),
                DeviceSignature = []
            };
            parameters = request with { DeviceSignature = [.. await SignAsync(request.GetSigningInput(Context), cancellationToken).ConfigureAwait(false)] };
        }
        AccountDeviceState state;
        async Task<AccountDeviceState> ReadAsync(bool accountSession)
        {
            var relayClient = accountSession
                ? await relayClients.GetAsync(relayId, accountSigner ?? throw new InvalidOperationException("An account signer is required for an account session."), cancellationToken).ConfigureAwait(false)
                : await relayClients.GetAsync(relayId, this, cancellationToken).ConfigureAwait(false);
            return await relayClient.SendHttpAsync<AccountDeviceState>(authorization is null ? HttpMethod.Get : HttpMethod.Post, "device.state.resolve", parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        try
        {
            try
            {
                try { state = await ReadAsync(useAccount).ConfigureAwait(false); }
                catch (RelayException exception) when (!useAccount && accountId == Options.AccountId && exception.Error.Code == "unauthorized")
                {
                    // A session can expire while the device remains authorized. The
                    // transport discarded that token; retry the same device identity.
                    state = await ReadAsync(false).ConfigureAwait(false);
                }
            }
            catch (RelayException exception) when (allowAccountFallback && !useAccount && accountId == Options.AccountId && accountSigner is not null && exception.Error.Code is "unauthorized" or "device_unknown")
            {
                state = await ReadAsync(true).ConfigureAwait(false);
            }
        }
        catch (RelayException exception) when (exception.Error.Code == "not_found")
        {
            return null;
        }
        if (state.Account != accountId)
            throw new InvalidDataException("The returned device state belongs to another account.");
        // A fully validated state is authoritative even when it revokes the supplied proof's signer.
        await SaveDeviceStateAsync(state, cancellationToken).ConfigureAwait(false);
        ValidateStateAuthorization(state, authorization);
        return state;
    }

    void ValidateStateAuthorization(AccountDeviceState state, TypedProtocolModel? authorization)
    {
        if (authorization is ContactInvite invite)
        {
            var inviteViolation = invite.Validate();
            if (inviteViolation is not null || state.ValidateDeviceAuthorization(invite.SignerDeviceId, Context) is not null)
                throw new InvalidDataException(inviteViolation?.Message ?? "The invitation signer is no longer authorized.");
            var certificate = state.Certificates.Single(value => value.GetDeviceId(Context) == invite.SignerDeviceId);
            if (!Ed25519.Verify(invite.DeviceSignature.AsSpan(), certificate.SigningPublicKey.AsSpan(), invite.GetSigningInput(Context)))
                throw new CryptographicException("The invitation signature is invalid.");
        }
        else if (authorization is ContactGrant grant)
        {
            var now = Clock.UtcNow.ToUnixTimeSeconds();
            var input = grant.GetSigningInput(Context);
            if (grant.ExpiresAt <= now || !state.Certificates.Any(certificate => certificate.NotBefore <= now && certificate.ExpiresAt > now
                && grant.Signatures.TryGetValue(certificate.GetDeviceId(Context), out var signature) && Ed25519.Verify(signature.AsSpan(), certificate.SigningPublicKey.AsSpan(), input)))
                throw new CryptographicException("The contact grant has no signature from a currently authorized device.");
        }
    }

    async Task SaveDeviceStateAsync(AccountDeviceState state, CancellationToken cancellationToken, StateUpdateAttempt? attempt = null, bool published = false)
    {
        var changed = false;
        Task retirement = Task.CompletedTask;
        async Task SaveAsync()
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var json = state.ToJson();
                await using var database = new MeshlineDbContext(databaseOptions);
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var record = await database.DeviceStates.SingleOrDefaultAsync(value => value.AccountId == state.Account, cancellationToken).ConfigureAwait(false);
                if (record is not null)
                {
                    if (state.Revision < record.Revision || state.Revision == record.Revision && json != record.DocumentJson)
                        throw new InvalidDataException("The device state is older than the known state or conflicts at the same revision.");
                    record.DocumentJson = json;
                    record.Revision = state.Revision;
                }
                else
                    database.DeviceStates.Add(new DeviceStateRecord { AccountId = state.Account, DocumentJson = json, Revision = state.Revision });
                if (state.Account == Options.AccountId)
                {
                    var pending = await database.SignedRequests.SingleOrDefaultAsync(value => value.Method == "device.state.publish", cancellationToken).ConfigureAwait(false);
                    if (pending is { Pending: true } && (pending.IntentJson is null || pending.RequestState.IsCurrent())
                        && (state.Revision > pending.Revision || state.Revision == pending.Revision && json == pending.DocumentJson))
                    {
                        pending.Pending = false;
                        pending.IntentJson = null;
                    }
                }
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                CacheDeviceState(state, published ? Clock.UtcNow : null);
                if (state.Account == Options.AccountId)
                {
                    changed = DeviceState?.Revision != state.Revision;
                    DeviceState = state;
                    if (Local is { } certificate && state.ValidateDeviceAuthorization(certificate.GetDeviceId(Context), Context) is not null)
                        retirement = relayClients.InvalidateDeviceAsync();
                }
            }
            finally
            {
                _stateGate.Release();
            }
        }
        if (attempt is null) await SaveAsync().ConfigureAwait(false);
        else await attempt.CurrentAsync(SaveAsync, cancellationToken).ConfigureAwait(false);
        // Retirement detaches the old sessions during the guarded commit. Drain them
        // outside both state gates so a successor can publish and create new sessions.
        try
        {
            if (changed) DeviceStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { await retirement.ConfigureAwait(false); }
    }

    async Task UseHomeRelayAsync(string relayId, CancellationToken cancellationToken)
    {
        var relayClient = await relayClients.GetAsync(relayId, this, cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(_homeRelayClient, relayClient))
            return;
        var descriptor = await relayClient.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        var previous = _homeRelayClient;
        SetHomeRelayClient(relayClient);
        try
        {
            if (descriptor.Endpoints.Any(static value => value.StartsWith("wss://", StringComparison.Ordinal)))
                relayClient.StartNotifications();
        }
        catch
        {
            SetHomeRelayClient(previous);
            _requiredDeviceRevisions.TryRemove(relayId, out _);
            throw;
        }
        if (previous is not null && previous.RelayId != relayId)
            _requiredDeviceRevisions.TryRemove(previous.RelayId, out _);
    }

    void SetHomeRelayClient(RelayClient? relayClient)
    {
        lock (_homeGate)
        {
            if (_homeRelayClient is { } previous)
            {
                previous.NotificationReceived -= OnDeviceStateNotification;
                previous.ErrorOccurred -= OnHomeConnectionError;
            }
            _homeRelayClient = relayClient;
            if (relayClient is not null)
            {
                relayClient.NotificationReceived += OnDeviceStateNotification;
                relayClient.ErrorOccurred += OnHomeConnectionError;
            }
        }
    }

    void OnHomeRouteChanged(object? sender, EventArgs args) => _refreshRequests?.Writer.TryWrite(true);

    void OnHomeRelayChanged(object? sender, RelayClient relayClient)
    {
        lock (_homeGate)
            if (_homeRelayClient is { } current && !ReferenceEquals(current, relayClient) && current.RelayId == relayClient.RelayId
                && relayClient.SessionMode == SessionMode.Device && relayClient.AuthenticationState == RelayAuthenticationState.Device)
                _refreshRequests?.Writer.TryWrite(true);
    }

    async Task RefreshHomeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reader = _refreshRequests!.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out _)) { }
                if (accountSigner is null && DeviceState?.ValidateDeviceAuthorization(Local!.GetDeviceId(Context), Context) is not null)
                    continue;
                try
                {
                    var relayId = await GetHomeRelayIdAsync(cancellationToken).ConfigureAwait(false);
                    await UseHomeRelayAsync(relayId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    ReportBackgroundError(BackgroundOperation.Synchronize, _homeRelayClient?.RelayId, exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    void OnDeviceStateNotification(object? sender, RpcRequest request)
    {
        lock (_homeGate)
        {
            if (_homeRelayClient is not { } relayClient || !ReferenceEquals(sender, relayClient) || request.Method != "device.state.changed")
                return;
            if (request.Params is null || !request.Params.TryGetValue("revision", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var revision) || revision < 0)
                throw new InvalidDataException("A device state notification must contain a nonnegative revision.");
            _requiredDeviceRevisions.AddOrUpdate(relayClient.RelayId, revision, (_, previous) => Math.Max(previous, revision));
        }
    }

    void OnHomeConnectionError(object? sender, Exception error)
    {
        lock (_homeGate)
            if (_homeRelayClient is { } relayClient && ReferenceEquals(sender, relayClient))
                ReportBackgroundError(BackgroundOperation.Connect, relayClient.RelayId, error);
    }
}
