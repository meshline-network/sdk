using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Transport;

/// <summary>
/// Owns and shares relay clients for one network, account, and device, with separate account and device sessions.
/// </summary>
/// <remarks>
/// Share one pool among components for the same network, account, and device. The first device-authenticated request binds the pool to that device. Account and device sessions are kept separate. Dispose dependent components before disposing the pool.
/// </remarks>
public sealed class RelayClientPool : IAsyncDisposable
{
    /// <summary>The named/keyed HTTP client used by constructor injection.</summary>
    public const string HttpClientName = nameof(RelayClientPool);

    /// <summary>
    /// Occurs when the set of clients owned by the pool changes.
    /// </summary>
    public event EventHandler? PoolChanged;
    /// <summary>
    /// Occurs when a pooled relay client's identity, session, connection, authentication, or error state changes.
    /// </summary>
    public event EventHandler<RelayClient>? RelayChanged;

    readonly Lock _gate = new();
    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly Dictionary<string, List<Entry>> _entries = new(StringComparer.Ordinal);
    string? _deviceId;
    readonly List<Task> _retirements = [];
    readonly CancellationTokenSource _lifetime = new();
    Task? _disposal;

    /// <summary>
    /// The network reference and registry contract identifying this Meshline network.
    /// </summary>
    public NetworkContext Context { get; }
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public string AccountId { get; }
    /// <summary>
    /// The application-provided registry used for relay discovery.
    /// </summary>
    public IRelayRegistry Registry { get; }
    /// <summary>
    /// A read-only snapshot of the clients currently held by the pool; the pool retains ownership.
    /// </summary>
    public IReadOnlyList<RelayClient> Clients
    {
        get
        {
            lock (_gate)
                return _entries.Values.SelectMany(static entries => entries).Select(static entry => entry.Client).ToArray().AsReadOnly();
        }
    }

    /// <summary>
    /// Initializes a new instance of <see cref="RelayClientPool"/>.
    /// </summary>
    /// <param name="options">The network and account configuration for this component.</param>
    /// <param name="registry">The application's relay registry for the configured network.</param>
    /// <param name="httpClient">An optional caller-owned HTTP client used for requests and WebSocket handshakes; it must outlive the pool. When omitted, the pool creates and owns a client.</param>
    /// <remarks>
    /// A supplied HTTP client is used unchanged. Configure its pipeline to disable redirects, cookies, and implicit business-request retries. The pool creates and owns each WebSocket. Keyed dependency injection selects <see cref="HttpClientName"/>; the optional null default creates a pool-owned HTTP client.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
    /// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
    public RelayClientPool(ClientOptions options, IRelayRegistry registry, [FromKeyedServices(HttpClientName)] HttpClient? httpClient = null)
    {
        options.Validate();
        Context = options.Context;
        AccountId = options.AccountId;
        Registry = registry;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Gets a shared relay client suitable for the requested authorization mode.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A pool-owned shared client; callers must leave its lifetime management to the pool.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed or the selected client is retired while it is being acquired.</exception>
    /// <exception cref="ArgumentException">The relay identifier is invalid.</exception>
    public async Task<RelayClient> GetAsync(string relayId, CancellationToken cancellationToken) =>
        (await GetEntryAsync(relayId, null, null, cancellationToken).ConfigureAwait(false)).Client;

    /// <summary>
    /// Gets a shared relay client suitable for the requested authorization mode.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="signer">The account signer for an account session.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A pool-owned shared client; callers must leave its lifetime management to the pool.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed or the selected client is retired while it is being acquired.</exception>
    /// <exception cref="ArgumentException">The relay identifier is invalid. The signer belongs to a different account than this pool.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="CryptographicException">The configured signer or cryptographic provider fails while signing an authentication proof or verifying relay evidence.</exception>
    /// <exception cref="InvalidOperationException">The relay is not registered as active, the signer's identity changes, or the pool is already bound to another device.</exception>
    public async Task<RelayClient> GetAsync(string relayId, IAccountSigner signer, CancellationToken cancellationToken)
    {
        if (signer.AccountId != AccountId)
            throw new ArgumentException("The signer belongs to another account.", nameof(signer));
        return (await GetEntryAsync(relayId, SessionMode.Account, (client, token) => signer.AccountId == AccountId
            ? client.AuthenticateAsync(signer, token)
            : throw new InvalidOperationException("The relay client's account identity has changed."), cancellationToken).ConfigureAwait(false)).Client;
    }

    /// <summary>
    /// Gets a shared relay client suitable for the requested authorization mode.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="signer">The device signer for a device session; the pool binds to its device identity.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A pool-owned shared client; callers must leave its lifetime management to the pool.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed or the selected client is retired while it is being acquired.</exception>
    /// <exception cref="ArgumentException">The relay identifier is invalid. The signer belongs to a different account than this pool.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="CryptographicException">The configured signer or cryptographic provider fails while signing an authentication proof or verifying relay evidence.</exception>
    /// <exception cref="InvalidOperationException">The relay is not registered as active, the signer's identity changes, or the pool is already bound to another device.</exception>
    public async Task<RelayClient> GetAsync(string relayId, IDeviceSigner signer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var certificate = signer.Certificate;
        if (certificate.Account != AccountId)
            throw new ArgumentException("The device belongs to another account.", nameof(signer));
        var deviceId = certificate.GetDeviceId(Context);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_deviceId is not null && _deviceId != deviceId)
                throw new InvalidOperationException("The relay client pool is already bound to another device.");
            _deviceId = deviceId;
        }
        return (await GetEntryAsync(relayId, SessionMode.Device, (client, token) =>
        {
            var current = signer.Certificate;
            if (current.Account != AccountId || current.GetDeviceId(Context) != deviceId)
                throw new InvalidOperationException("The relay client's device identity has changed.");
            return client.AuthenticateAsync(signer, token);
        }, cancellationToken).ConfigureAwait(false)).Client;
    }

    async Task<Entry> GetEntryAsync(string relayId, SessionMode? mode, Func<RelayClient, CancellationToken, Task>? authenticate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        Task authentication;
        var added = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _entries.TryGetValue(relayId, out var entries);
            entry = (mode is null ? entries?.FirstOrDefault()
                : entries?.FirstOrDefault(candidate => candidate.Mode == mode) ?? entries?.FirstOrDefault(static candidate => candidate.Mode is null))!;
            if (entry is null)
            {
                entry = new(new(relayId, Registry, _http), _lifetime.Token);
                entry.Client.StateChanged += OnRelayStateChanged;
                entry.Client.Faulted += OnClientFaulted;
                if (entries is null)
                    _entries.Add(relayId, entries = []);
                entries.Add(entry);
                added = true;
            }
            if (mode is not null && entry.Mode is null)
            {
                entry.Mode = mode;
                entry.Authentication = AuthenticateAsync(entry, authenticate!);
                _ = entry.Authentication.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            authentication = mode is null ? Task.CompletedTask : entry.Authentication;
        }
        if (added)
            PoolChanged?.Invoke(this, EventArgs.Empty);
        await authentication.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            ObjectDisposedException.ThrowIf(entry.Retired, this);
        return entry;
    }

    async Task AuthenticateAsync(Entry entry, Func<RelayClient, CancellationToken, Task> authenticate)
    {
        await Task.Yield();
        try
        {
            await authenticate(entry.Client, entry.Lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
                entry.Mode = entry.Client.SessionMode;
            throw;
        }
    }

    void Retire(Entry entry)
    {
        if (entry.Retired)
            return;
        entry.Retired = true;
        if (_entries.TryGetValue(entry.Client.RelayId, out var entries))
        {
            entries.Remove(entry);
            if (entries.Count == 0)
                _entries.Remove(entry.Client.RelayId);
        }
        entry.Disposal = DisposeEntryAsync(entry);
        _retirements.RemoveAll(static task => task.IsCompletedSuccessfully);
        _retirements.Add(entry.Disposal);
    }

    async Task DisposeEntryAsync(Entry entry)
    {
        await Task.Yield();
        try
        {
            await entry.Lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await entry.Client.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try { await entry.Authentication.ConfigureAwait(false); }
                catch (Exception) { /* Authentication failures are reported to callers. */ }
            }
        }
        finally
        {
            entry.Client.StateChanged -= OnRelayStateChanged;
            entry.Client.Faulted -= OnClientFaulted;
            entry.Lifetime.Dispose();
            PoolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    void OnRelayStateChanged(object? sender, EventArgs args) => RelayChanged?.Invoke(this, (RelayClient)sender!);

    void OnClientFaulted(object? sender, EventArgs args)
    {
        var client = (RelayClient)sender!;
        lock (_gate)
            if (_entries.TryGetValue(client.RelayId, out var entries) && entries.FirstOrDefault(entry => ReferenceEquals(entry.Client, client)) is { } entry)
                Retire(entry);
    }

    internal async Task InvalidateDeviceAsync()
    {
        List<Task> tasks = [];
        lock (_gate)
        {
            foreach (var entry in _entries.Values.SelectMany(static entries => entries).Where(static entry => entry.Mode == SessionMode.Device).ToArray())
            {
                Retire(entry);
                tasks.Add(entry.Disposal!);
            }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels pending authentication and relay work, disposes pooled clients, and releases any pool-owned HTTP client.
    /// </summary>
    /// <returns>A value task that completes when owned resources and active work have been released.</returns>
    /// <exception cref="AggregateException">A registered cancellation callback throws while pool or session lifetimes are canceled.</exception>
    public async ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_gate)
            disposal = _disposal ??= DisposeCoreAsync();
        await disposal.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    async Task DisposeCoreAsync()
    {
        await Task.Yield();
        Task[] tasks;
        lock (_gate)
        {
            foreach (var entry in _entries.Values.SelectMany(static entries => entries).ToArray())
                Retire(entry);
            tasks = _retirements.ToArray();
        }
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            finally { await Task.WhenAll(tasks).ConfigureAwait(false); }
        }
        finally
        {
            _lifetime.Dispose();
            if (_ownsHttp) _http.Dispose();
        }
    }

    sealed class Entry(RelayClient client, CancellationToken cancellationToken)
    {
        public RelayClient Client { get; } = client;
        public SessionMode? Mode { get; set; }
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public Task Authentication { get; set; } = Task.CompletedTask;
        public Task? Disposal { get; set; }
        public bool Retired { get; set; }
    }
}
