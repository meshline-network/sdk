using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline;

/// <summary>
/// Coordinates account, device, profile, messaging, channel, and group components for one local account and device.
/// </summary>
/// <remarks>
/// Migrate the database explicitly before calling <see cref="ClientComponent.InitializeAsync"/>. Establish or recover account authorization before starting a new device. The client owns its components; the application owns the supplied pool, signer, and secret protector. Events may be raised from background threads.
/// </remarks>
public sealed partial class MeshlineClient : ClientComponent
{
    /// <summary>
    /// Occurs when a locally computed conversation summary is created, updated, or removed.
    /// </summary>
    public event EventHandler<ConversationChangedEventArgs>? ConversationChanged;

    readonly DatabaseOptions _databaseOptions;
    readonly RelayClientPool _relayClients;
    readonly SemaphoreSlim _accountGate = new(1, 1);

    /// <summary>
    /// The current account's last known route, or <see langword="null"/> when no route is known.
    /// </summary>
    public AccountRoute? Route => AccountManager.Route;
    /// <summary>
    /// The local device certificate, or <see langword="null"/> when no local device has been loaded or created.
    /// </summary>
    public DeviceCertificate? Device => DeviceManager.Local;
    /// <summary>
    /// The currently known account device state, or <see langword="null"/> when it is unavailable.
    /// </summary>
    public AccountDeviceState? DeviceState => DeviceManager.DeviceState;
    /// <summary>
    /// The current account's profile, or <see langword="null"/> when it is unavailable.
    /// </summary>
    public AccountProfile? Profile => ProfileManager.Profile;
    /// <summary>
    /// The client-owned component for account routing.
    /// </summary>
    public AccountManager AccountManager { get; }
    /// <summary>
    /// The client-owned component for local device keys and authorization.
    /// </summary>
    public DeviceManager DeviceManager { get; }
    /// <summary>
    /// The client-owned component for account profiles.
    /// </summary>
    public ProfileManager ProfileManager { get; }
    /// <summary>
    /// The client-owned component for contacts and direct messaging.
    /// </summary>
    public MessageManager MessageManager { get; }
    /// <summary>
    /// The client-owned component for public channels.
    /// </summary>
    public ChannelManager ChannelManager { get; }
    /// <summary>
    /// The client-owned component for encrypted groups.
    /// </summary>
    public GroupManager GroupManager { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="MeshlineClient"/>.
    /// </summary>
    /// <param name="options">The network and account configuration for this component.</param>
    /// <param name="databaseOptions">The SQLite database configuration; create its parent directory and apply migrations before initialization.</param>
    /// <param name="relayClients">The shared relay pool. The application owns it and must dispose it after all dependent components.</param>
    /// <param name="secretProtector">The application-owned protector used to store and restore local secrets.</param>
    /// <param name="accountSigner">The application-owned account signer required for account-authorized operations, or <see langword="null"/> when those operations are not needed.</param>
    /// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null. The <paramref name="options"/> argument is null. The <paramref name="secretProtector"/> argument is null.</exception>
    /// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
    /// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
    public MeshlineClient(ClientOptions options, DatabaseOptions databaseOptions, RelayClientPool relayClients, ISecretProtector secretProtector, IAccountSigner? accountSigner = null)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(secretProtector);
        _databaseOptions = databaseOptions;
        _relayClients = relayClients;
        AccountManager = new AccountManager(options, databaseOptions, relayClients, accountSigner);
        DeviceManager = new DeviceManager(options, databaseOptions, relayClients, AccountManager, accountSigner, secretProtector);
        ProfileManager = new ProfileManager(options, databaseOptions, relayClients, AccountManager, DeviceManager);
        MessageManager = new MessageManager(options, databaseOptions, relayClients, AccountManager, DeviceManager, secretProtector);
        ChannelManager = new ChannelManager(options, databaseOptions, relayClients, DeviceManager);
        GroupManager = new GroupManager(options, databaseOptions, relayClients, DeviceManager, MessageManager, secretProtector);
        AccountManager.BackgroundError += OnComponentError;
        DeviceManager.BackgroundError += OnComponentError;
        ProfileManager.BackgroundError += OnComponentError;
        ChannelManager.BackgroundError += OnComponentError;
        MessageManager.BackgroundError += OnComponentError;
        GroupManager.BackgroundError += OnComponentError;
        MessageManager.MessageReceived += OnMessageReceived;
        MessageManager.SendStatusChanged += OnMessageSendStatusChanged;
        ChannelManager.TimelineChanged += OnChannelTimelineChanged;
        ChannelManager.FollowChanged += OnChannelFollowChanged;
        GroupManager.TimelineChanged += OnGroupTimelineChanged;
        GroupManager.GroupChanged += OnGroupChanged;
    }

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">The database is bound to another network or account, or a required dependency has not been initialized.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="InvalidDataException">A child component finds inconsistent persisted device state.</exception>
    /// <exception cref="ObjectDisposedException">A child component has already been disposed.</exception>
    protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        await AccountManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await DeviceManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ProfileManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await MessageManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ChannelManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await GroupManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await InitializeConversationsAsync(cancellationToken).ConfigureAwait(false);
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
    /// <exception cref="UnauthorizedAccessException">The local device is not authorized to enqueue the account synchronization message.</exception>
    /// <exception cref="ArgumentException">A pending home-relay migration contains invalid publication input.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A pending home-relay migration requires a validity interval or revision outside the supported range.</exception>
    /// <exception cref="NotSupportedException">A pending account operation contains an unsupported account namespace.</exception>
    /// <exception cref="AggregateException">A child component fails during cancellation of an unsuccessful startup.</exception>
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        await AccountManager.StartAsync(cancellationToken).ConfigureAwait(false);
        await ResumeHomeRelayMigrationAsync(cancellationToken).ConfigureAwait(false);
        await DeviceManager.StartAsync(cancellationToken).ConfigureAwait(false);
        await ProfileManager.StartAsync(cancellationToken).ConfigureAwait(false);
        await MessageManager.StartAsync(cancellationToken).ConfigureAwait(false);
        await ChannelManager.StartAsync(cancellationToken).ConfigureAwait(false);
        await GroupManager.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">A child component has already been disposed.</exception>
    /// <exception cref="AggregateException">A child component runtime cancellation callback throws during shutdown.</exception>
    protected override async Task OnStopAsync()
    {
        await GroupManager.StopAsync().ConfigureAwait(false);
        await ChannelManager.StopAsync().ConfigureAwait(false);
        await MessageManager.StopAsync().ConfigureAwait(false);
        await ProfileManager.StopAsync().ConfigureAwait(false);
        await DeviceManager.StopAsync().ConfigureAwait(false);
        await AccountManager.StopAsync().ConfigureAwait(false);
    }

    void OnComponentError(object? sender, BackgroundErrorEventArgs args) => ReportBackgroundError(args.Operation, args.Resource, args.Error);

    /// <summary>
    /// Creates and authorizes a local device and establishes the account's initial home-relay route.
    /// </summary>
    /// <param name="options">Optional relay selection and validity settings; omitted settings use their defaults.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// Requires initialization, an account signer, and protected local key storage. Interrupted establishment resumes using the same database and selected relay. An existing route without valid local-device authorization requires explicit account recovery.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. A verified active relay, account signer, complete device state, or local-device authorization required by the operation is unavailable. A route is already known without valid local-device authorization, or an interrupted establishment targets another relay.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The target relay, previous device state, or publication input is invalid or belongs to another account.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A certificate or route validity duration, or a selected publication revision, is outside its supported range.</exception>
    public async Task EstablishAccountAsync(AccountEstablishmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        options ??= new();
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var route = await AccountManager.RefreshRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (route is not null)
            {
                if (Device is not null)
                {
                    var state = await DeviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (state is not null && state.ValidateDeviceAuthorization(Device.GetDeviceId(Context), Context) is null)
                    {
                        await using var completed = new MeshlineDbContext(_databaseOptions);
                        await completed.AccountEstablishments.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
                throw new InvalidOperationException("The account already has a route. Start the existing account or explicitly recover it.");
            }
            if (Route is not null)
                throw new InvalidOperationException("A previous account route is known. Use account recovery instead of initial establishment.");
            await using var database = new MeshlineDbContext(_databaseOptions);
            var establishment = await database.AccountEstablishments.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (establishment is not null && options.RelayId is not null && establishment.RelayId != options.RelayId)
                throw new InvalidOperationException("Resume establishment at its original relay, or recover the account explicitly.");
            var relayId = await SelectRelayAsync(establishment?.RelayId ?? options.RelayId, cancellationToken).ConfigureAwait(false);
            if (establishment is null)
            {
                establishment = new() { RelayId = relayId };
                database.AccountEstablishments.Add(establishment);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            var certificate = await PrepareLocalDeviceAsync(options.CertificateValidity, cancellationToken).ConfigureAwait(false);
            var publication = await DeviceManager.PublishDeviceStateAsync(relayId, [certificate], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (publication.Status != DeviceStatePublishStatus.Staged)
                throw new InvalidOperationException("The relay already treats this account as established. Resolve the current route before continuing.");
            await AccountManager.PublishRouteAsync(relayId, options.RouteValidity, cancellationToken: cancellationToken).ConfigureAwait(false);
            await ConfirmLocalAuthorizationAsync(relayId, cancellationToken).ConfigureAwait(false);
            database.AccountEstablishments.Remove(establishment);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accountGate.Release();
        }
    }

    /// <summary>
    /// Explicitly restores account access by publishing device state and a new account route.
    /// </summary>
    /// <param name="options">Optional relay selection, validity, prior device state, and recovery revision settings.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// Requires initialization and an account signer. Recovery can publish new authoritative device and route revisions and clears pending establishment and migration records after success. Use this explicitly when restoring access, not as a routine startup fallback.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. A verified active relay, account signer, complete device state, or local-device authorization required by the operation is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The target relay, previous device state, or publication input is invalid or belongs to another account.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A certificate or route validity duration, or a selected publication revision, is outside its supported range.</exception>
    public async Task RecoverAccountAsync(AccountRecoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        options ??= new();
        if (options.PreviousDeviceState is { } supplied && (supplied.Account != Options.AccountId || supplied.Validate(Context) is not null))
            throw new ArgumentException("The previous device state must be valid and belong to this account.", nameof(options));
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountRoute? route = null;
            try
            {
                route = await AccountManager.RefreshRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or RelayException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                ReportBackgroundError(BackgroundOperation.Connect, route?.RelayId, exception);
            }
            string relayId;
            if (options.RelayId is null && route is not null)
            {
                try
                {
                    relayId = await SelectRelayAsync(route.RelayId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException or RelayException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    ReportBackgroundError(BackgroundOperation.Connect, route.RelayId, exception);
                    relayId = await SelectRelayAsync(null, cancellationToken).ConfigureAwait(false);
                }
            }
            else
                relayId = await SelectRelayAsync(options.RelayId, cancellationToken).ConfigureAwait(false);
            try
            {
                if (Route is { } knownRoute) await DeviceManager.GetOwnDeviceStateAsync(knownRoute.RelayId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or RelayException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                ReportBackgroundError(BackgroundOperation.Connect, Route?.RelayId, exception);
            }
            await PrepareLocalDeviceAsync(options.CertificateValidity, cancellationToken).ConfigureAwait(false);
            await DeviceManager.PublishDeviceStateAsync(relayId, options: new() { PreviousState = options.PreviousDeviceState, IsRecovery = true, Revision = options.DeviceStateRevision }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await AccountManager.PublishRouteAsync(relayId, options.RouteValidity, options.RouteRevision, isRecovery: true, cancellationToken).ConfigureAwait(false);
            await ConfirmLocalAuthorizationAsync(relayId, cancellationToken).ConfigureAwait(false);
            await using var database = new MeshlineDbContext(_databaseOptions);
            await database.HomeRelayMigrations.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await database.AccountEstablishments.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accountGate.Release();
        }
    }

    /// <inheritdoc/>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="AggregateException">A cancellation callback throws while dependent component or relay lifetimes are canceled.</exception>
    protected override async ValueTask DisposeAsyncCore()
    {
        MessageManager.MessageReceived -= OnMessageReceived;
        MessageManager.SendStatusChanged -= OnMessageSendStatusChanged;
        ChannelManager.TimelineChanged -= OnChannelTimelineChanged;
        ChannelManager.FollowChanged -= OnChannelFollowChanged;
        GroupManager.TimelineChanged -= OnGroupTimelineChanged;
        GroupManager.GroupChanged -= OnGroupChanged;
        await _conversationLifetime.CancelAsync().ConfigureAwait(false);
        await _conversationObserver.ConfigureAwait(false);
        _conversationLifetime.Dispose();
        await GroupManager.DisposeAsync().ConfigureAwait(false);
        await ChannelManager.DisposeAsync().ConfigureAwait(false);
        await MessageManager.DisposeAsync().ConfigureAwait(false);
        await ProfileManager.DisposeAsync().ConfigureAwait(false);
        await DeviceManager.DisposeAsync().ConfigureAwait(false);
        await AccountManager.DisposeAsync().ConfigureAwait(false);
        AccountManager.BackgroundError -= OnComponentError;
        DeviceManager.BackgroundError -= OnComponentError;
        ProfileManager.BackgroundError -= OnComponentError;
        ChannelManager.BackgroundError -= OnComponentError;
        MessageManager.BackgroundError -= OnComponentError;
        GroupManager.BackgroundError -= OnComponentError;
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}
