using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline;

/// <summary>
/// Coordinates account, device, profile, messaging, channel, and group components for one local account and device.
/// </summary>
/// <remarks>
/// <para>Migrate the database before <see cref="ClientComponent.InitializeAsync"/>, establish authorization when needed,
/// then call <see cref="ClientComponent.StartAsync"/> separately. Startup awaits required network authentication;
/// local history is available after initialization. The client owns its components; the application owns the supplied pool,
/// signer and secret protector. Events may be raised from background threads.</para>
/// <para>Verified local-device removal or rejected local authentication suspends background work. An own-state query
/// denied after device-session renewal also suspends it, preserving the last verified state without declaring revocation.
/// Background reads never request account signatures. Observe BackgroundError for the cause and StateChanged for completed shutdown.</para>
/// <para>Use one active client per database, including across processes, and await <see cref="ClientComponent.DisposeAsync"/>
/// before transferring ownership. The SDK does not enforce exclusivity. Concurrent calls follow each method's calling contract;
/// <see cref="EstablishAccountAsync"/> must not overlap direct device mutations.</para>
/// </remarks>
public sealed partial class MeshlineClient : ClientComponent
{
    /// <summary>
    /// Occurs when a locally computed conversation summary is created, updated, or removed.
    /// </summary>
    public event EventHandler<ConversationChangedEventArgs>? ConversationChanged;

    readonly DatabaseOptions _databaseOptions;
    readonly IAccountSigner? _accountSigner;
    readonly RelayClientPool _relayClients;
    readonly SemaphoreSlim _accountGate = new(1, 1);
    // Includes startup before the base component reports Running, and cleanup
    // after failed startup. It is cleared only after all child runtimes stop.
    volatile bool _runtimeActive;
    bool _startWaitCanceled;
    Action? _requestAuthorizationStop;

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
        _accountSigner = accountSigner;
        _relayClients = relayClients;
        AccountManager = new AccountManager(options, databaseOptions, relayClients, accountSigner);
        DeviceManager = new DeviceManager(options, databaseOptions, relayClients, AccountManager, accountSigner, secretProtector);
        ProfileManager = new ProfileManager(options, databaseOptions, relayClients, AccountManager, DeviceManager);
        MessageManager = new MessageManager(options, databaseOptions, relayClients, AccountManager, DeviceManager, secretProtector);
        ChannelManager = new ChannelManager(options, databaseOptions, relayClients, DeviceManager);
        GroupManager = new GroupManager(options, databaseOptions, relayClients, DeviceManager, MessageManager, secretProtector);
        AccountManager.BackgroundError += OnComponentError;
        DeviceManager.BackgroundError += OnComponentError;
        DeviceManager.DeviceStateChanged += OnDeviceAuthorizationChanged;
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
        // Wait for an executing setup, then reserve the runtime before starting
        // children. Do not hold the account gate through migration or callbacks.
        try { await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // The base lifecycle invokes OnStop after any failed start. No child
            // was started here; do not cancel the setup's active metadata reads.
            _startWaitCanceled = true;
            throw;
        }
        try { _runtimeActive = true; }
        finally { _accountGate.Release(); }
        var runtimeToken = RuntimeCancellationToken;
        var stopRequested = 0;
        _requestAuthorizationStop = () =>
        {
            if (Interlocked.Exchange(ref stopRequested, 1) == 0)
                _ = StopUnauthorizedDeviceAsync(runtimeToken);
        };
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
    /// <exception cref="AggregateException">Cancellation callbacks or multiple child components fail; all children are stopped before the errors are reported.</exception>
    protected override async Task OnStopAsync()
    {
        if (_startWaitCanceled)
        {
            _startWaitCanceled = false;
            return;
        }
        _requestAuthorizationStop = null;
        List<Exception> failures = [];
        ClientComponent[] components = [GroupManager, ChannelManager, MessageManager, ProfileManager, DeviceManager, AccountManager];
        foreach (var component in components)
        {
            try { await component.StopAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        _runtimeActive = false;
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    void OnDeviceAuthorizationChanged(object? sender, EventArgs args)
    {
        var requestStop = _requestAuthorizationStop;
        if (requestStop is null || Device is not { } device || DeviceState is not { } state) return;
        var deviceId = device.GetDeviceId(Context);
        if (!state.Certificates.Any(value => value.GetDeviceId(Context) == deviceId))
            requestStop();
    }

    async Task StopUnauthorizedDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var operation = BeginOperation(ref cancellationToken);
            // The event can be raised by a child task which shutdown must drain.
            // Return to that task before stopping, and bind the queued stop to this
            // runtime so it cannot stop a later start or deadlock with disposal.
            await Task.Yield();
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception)
        {
            ReportBackgroundError(BackgroundOperation.Synchronize, Options.AccountId, exception);
        }
    }

    void OnComponentError(object? sender, BackgroundErrorEventArgs args)
    {
        // Suspend unusable local-device access without rewriting verified state or
        // treating peer errors, network failures, or socket-token expiry as revocation.
        if (ReferenceEquals(sender, DeviceManager) && args.Error is RelayException error
            && (args.Resource == Options.AccountId && error.Error.Code is "unauthorized" or "device_unknown"
                || args.Operation == BackgroundOperation.Connect && error.Error.Code == "device_unknown"))
            _requestAuthorizationStop?.Invoke();
        ReportBackgroundError(args.Operation, args.Resource, args.Error);
    }

    /// <summary>
    /// Idempotently establishes or renews local-device authorization while preserving the account's other devices and original relay.
    /// </summary>
    /// <param name="options">Optional initial settings and explicit device replacement. An unfinished operation retains its saved lifetimes and profile.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes after confirming effective route/device authorization and initializing the profile as needed.</returns>
    /// <remarks>
    /// <para>Requires initialization. Valid authorization is reused; account signing is requested only for authorization changes
    /// or account-authenticated state access. Repeat this method with the same database to resume interrupted setup.
    /// Start background synchronization separately through <see cref="ClientComponent.StartAsync"/>.</para>
    /// <para>Retain the database, device keys and protection material together, or begin with all local data absent.
    /// Partial permanent loss is unsupported. Key-access failures preserve records and never regenerate keys.
    /// Device reauthorization does not restore lost chat history.</para>
    /// <para>Stop synchronization before changing authorization. Do not overlap this method with
    /// CreateDeviceAsync, RenewDeviceAsync, RemoveDeviceAsync or PublishDeviceStateAsync on this client's DeviceManager,
    /// including from signing or event callbacks. Await completion, failure or cancellation before starting the other operation.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="InvalidOperationException">Initialization is incomplete, synchronization is running when authorization needs changing, no active relay is configured, or the pool is bound to an unrelated device.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="ArgumentException">The target relay, previous device state, or publication input is invalid or belongs to another account.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A certificate or route validity duration, or a selected publication revision, is outside its supported range.</exception>
    /// <exception cref="AccountEstablishmentException">Setup requires caller action, verified state is unavailable, or publication is not confirmed.
    /// Inspect Reason and the optional device, key-access or contact details. InnerException preserves signing, transport and validation failures.</exception>
    public async Task EstablishAccountAsync(AccountEstablishmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        ValidateEstablishmentOptions(options ??= new());
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EstablishAccountCoreAsync(options, cancellationToken).ConfigureAwait(false);
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
            await DeviceManager.PublishDeviceStateAsync(relayId,
                new() { PreviousState = options.PreviousDeviceState, IsRecovery = true, Revision = options.DeviceStateRevision },
                options.CertificateValidity, cancellationToken).ConfigureAwait(false);
            await AccountManager.PublishRouteAsync(relayId, options.RouteValidity, options.RouteRevision, isRecovery: true, cancellationToken).ConfigureAwait(false);
            await DeviceManager.ConfirmAuthorizationAsync(relayId, cancellationToken).ConfigureAwait(false);
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
