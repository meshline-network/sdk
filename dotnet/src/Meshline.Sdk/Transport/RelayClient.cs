using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Protocol;
using Meshline.Models.Registry;
using Meshline.Validation;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Transport;

/// <summary>
/// Sends HTTP and WebSocket protocol requests to one relay through an application-owned relay pool.
/// </summary>
/// <remarks>
/// Obtain instances from <see cref="RelayClientPool"/>. The pool owns their lifetime. Events run on the raising thread. Reconnection restores transport availability but does not replay business requests; use protocol-specific recovery or synchronization to resolve interrupted work.
/// </remarks>
public sealed partial class RelayClient
{
    /// <summary>
    /// Occurs when the relay connection, authentication, session, or last-error state changes.
    /// </summary>
    public event EventHandler? StateChanged;

    static readonly UTF8Encoding Utf8 = new(false, true);
    readonly HttpClient _http;
    readonly TimeProvider Clock = Meshline.Clock.Provider;
    readonly IRelayRegistry _registry;
    readonly SemaphoreSlim _discoveryGate = new(1, 1);
    readonly SemaphoreSlim _authenticationGate = new(1, 1);
    readonly SemaphoreSlim _httpAuthenticationGate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    readonly Lock _backoffLock = new();
    readonly Lock _stateGate = new();
    SessionMode? _sessionMode;
    volatile RelayConnectionState _connectionState;
    volatile RelayAuthenticationState _authenticationState;
    volatile Exception? _lastError;
    RelayDescriptor? _descriptor;
    Uri? _httpEndpoint;
    AuthenticationIdentity? _identity;
    Session? _httpSession;
    Task<Session>? _httpAuthentication;
    string? _httpAuthenticationOrigin;
    long _backoffStarted;
    double _backoffSeconds;
    int _disposed;

    /// <summary>
    /// The relay's lowercase Neo script hash, including the <c>0x</c> prefix.
    /// </summary>
    public string RelayId { get; }
    /// <summary>
    /// The mode bound after successful authentication, or <see langword="null"/> before authentication; the binding survives expiry.
    /// </summary>
    public SessionMode? SessionMode
    {
        get
        {
            lock (_stateGate)
                return _sessionMode;
        }
    }
    /// <summary>
    /// The observed communication state; HTTP state does not imply a continuously monitored connection.
    /// </summary>
    public RelayConnectionState ConnectionState => _connectionState;
    /// <summary>
    /// The current authentication state, including expiry evaluated when this property is read.
    /// </summary>
    public RelayAuthenticationState AuthenticationState => _authenticationState is RelayAuthenticationState.Account or RelayAuthenticationState.Device && !HasValidSession
        ? RelayAuthenticationState.Expired : _authenticationState;
    /// <summary>
    /// The most recently recorded relay communication error, or <see langword="null"/> when none is recorded.
    /// </summary>
    public Exception? LastError => _lastError;

    internal RelayClient(string relayId, IRelayRegistry registry, HttpClient httpClient)
    {
        _http = httpClient;
        if (RelayIdentity.ValidateRelayId(relayId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(relayId));
        _registry = registry;
        RelayId = relayId;
    }

    internal Task AuthenticateAsync(IAccountSigner accountSigner, CancellationToken cancellationToken = default) =>
        EstablishSessionAsync(new(accountSigner), cancellationToken);

    internal Task AuthenticateAsync(IDeviceSigner deviceSigner, CancellationToken cancellationToken = default) =>
        EstablishSessionAsync(new(deviceSigner, _registry.Context), cancellationToken);

    async Task EstablishSessionAsync(AuthenticationIdentity identity, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _authenticationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_identity is not null)
                throw new InvalidOperationException("This relay client already has an authentication identity.");
            await GetDescriptorAsync(linked.Token).ConfigureAwait(false);
            var endpoint = _httpEndpoint!;
            _httpSession = await AuthenticateAsync(endpoint, (name, parameters, token) => SendHttpAsync(endpoint, HttpMethod.Post, name, parameters, null, token), identity, linked.Token).ConfigureAwait(false);
            _identity = identity;
            UpdateState(RelayConnectionState.Connected, identity.Mode == Models.Protocol.SessionMode.Account ? RelayAuthenticationState.Account : RelayAuthenticationState.Device, sessionMode: identity.Mode);
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    AuthenticationIdentity GetAuthenticationIdentity() => _identity
        ?? throw new InvalidOperationException("Establish a relay session before sending authenticated requests.");

    /// <summary>
    /// Retrieves and verifies the relay's descriptor, using the cached descriptor until refresh is needed.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The relay's verified, unexpired descriptor.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">This relay client, its pool, or its supplied HTTP client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The registry does not identify an active relay with the requested identity.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured HTTP or JSON-RPC protocol error.</exception>
    /// <exception cref="InvalidDataException">The response is missing, violates transport or model rules, or contains inconsistent relay evidence.</exception>
    /// <exception cref="JsonException">The request or response cannot be represented as protocol JSON.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="CryptographicException">The relay public-key signature cannot be verified by the cryptographic provider.</exception>
    public async Task<RelayDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _discoveryGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_descriptor is { } cached && cached.ExpiresAt > Clock.GetUtcNow().ToUnixTimeSeconds())
                return cached;
            var entry = await _registry.GetRelayAsync(RelayId, linked.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The relay is not registered.");
            if (entry.RelayId != RelayId || entry.Status != RelayStatus.Active)
                throw new InvalidOperationException("The registry does not identify an active relay with the requested ID.");
            var violation = RelayEndpointValidator.Validate(entry.Endpoint, out var kind, out _);
            if (violation is not null || kind != RelayEndpointKind.Https)
                throw new InvalidDataException(violation?.Message ?? "The relay discovery endpoint must use HTTPS.");
            var json = await SendHttpAsync(new(entry.Endpoint), HttpMethod.Get, "relay.descriptor", null, null, linked.Token).ConfigureAwait(false);
            var descriptor = ReadModel<RelayDescriptor>(json);
            if (descriptor.Validate(_registry.Context) is { } descriptorViolation)
                throw new InvalidDataException(descriptorViolation.Message);
            if (descriptor.RelayId != RelayId)
                throw new InvalidDataException("The descriptor does not identify the registered relay.");
            var endpoint = new Uri(descriptor.Endpoints.First(static value => value.StartsWith("https://", StringComparison.Ordinal)));
            if (_httpEndpoint is not null && GetOrigin(_httpEndpoint) != GetOrigin(endpoint))
                _httpSession = null;
            _httpEndpoint = endpoint;
            _descriptor = descriptor;
            return descriptor;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    /// <summary>
    /// Retrieves the relay's public information and advertised limits.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The relay's validated public information, including limits required by its capabilities.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">This relay client, its pool, or its supplied HTTP client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The registry does not identify an active relay with the requested identity.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured HTTP or JSON-RPC protocol error.</exception>
    /// <exception cref="InvalidDataException">The response is missing, violates transport or model rules, or contains inconsistent relay evidence.</exception>
    /// <exception cref="JsonException">The request or response cannot be represented as protocol JSON.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="CryptographicException">The relay public-key signature cannot be verified by the cryptographic provider.</exception>
    public async Task<RelayInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var descriptor = await GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        var info = await SendHttpAsync<RelayInfo>(HttpMethod.Get, "relay.info", authenticated: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (info.RelayId != RelayId)
            throw new InvalidDataException("The relay information does not identify the connected relay.");
        var channels = !descriptor.Capabilities.IsDefault && descriptor.Capabilities.Contains("channel.host.v1");
        var groups = !descriptor.Capabilities.IsDefault && descriptor.Capabilities.Contains("group.host.v1");
        var sockets = descriptor.Endpoints.Any(static value => value.StartsWith("wss://", StringComparison.Ordinal));
        if (channels && (info.Limits.ChannelTimelineRetention is null || sockets && info.Limits.MaxChannelSubscriptions is null)
            || groups && (info.Limits.GroupMessageRetention is null || info.Limits.MaxGroupMembers is null || info.Limits.MaxGroupInviteTtl is null || sockets && info.Limits.MaxGroupSubscriptions is null))
            throw new InvalidDataException("The relay information omits limits required by its advertised capabilities.");
        return info;
    }

    /// <summary>
    /// Sends an HTTP protocol request to the relay and checks the response for relay and protocol errors.
    /// </summary>
    /// <param name="method">The HTTP method required by the protocol operation.</param>
    /// <param name="name">The protocol operation name, such as <c>account.route.resolve</c>.</param>
    /// <param name="parameters">Optional named protocol parameters, serialized according to the transport operation.</param>
    /// <param name="authenticated">Whether to use the bound authenticated session; pass <see langword="false"/> for a public request.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <typeparam name="T">The protocol model type expected in the response.</typeparam>
    /// <returns>A task whose result is the deserialized response model after its protocol validation succeeds.</returns>
    /// <exception cref="InvalidOperationException">The relay is not registered as active, or the authentication identity required by the request is unavailable or has changed.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured HTTP or JSON-RPC protocol error.</exception>
    /// <exception cref="InvalidDataException">The response is missing, violates transport or model rules, or contains inconsistent relay evidence.</exception>
    /// <exception cref="JsonException">The request or response cannot be represented as protocol JSON.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">This relay client, its pool, or its supplied HTTP client has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="NotSupportedException">A requested response model or protocol value has no supported serializer.</exception>
    /// <exception cref="ArgumentException">An HTTP GET parameter is null or is not a scalar JSON value.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider or configured signer fails while verifying relay evidence or authenticating the request.</exception>
    public async Task<T> SendHttpAsync<T>(HttpMethod method, string name, ProtocolModel? parameters = null, bool authenticated = true, CancellationToken cancellationToken = default) where T : ProtocolModel
    {
        var json = await SendAuthenticatedHttpAsync(method, name, parameters, authenticated, cancellationToken).ConfigureAwait(false);
        var model = ReadModel<T>(json);
        if (model.Validate(_registry.Context) is { } violation)
            throw new InvalidDataException(violation.Message);
        return model;
    }

    /// <summary>
    /// Sends an HTTP protocol request to the relay and checks the response for relay and protocol errors.
    /// </summary>
    /// <param name="method">The HTTP method required by the protocol operation.</param>
    /// <param name="name">The protocol operation name, such as <c>account.route.resolve</c>.</param>
    /// <param name="parameters">Optional named protocol parameters, serialized according to the transport operation.</param>
    /// <param name="authenticated">Whether to use the bound authenticated session; pass <see langword="false"/> for a public request.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>The successful response must be HTTP 204 with no content.</remarks>
    /// <exception cref="InvalidOperationException">The relay is not registered as active, or the authentication identity required by the request is unavailable or has changed.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured HTTP or JSON-RPC protocol error.</exception>
    /// <exception cref="InvalidDataException">The response is missing, violates transport or model rules, or contains inconsistent relay evidence.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">This relay client, its pool, or its supplied HTTP client has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="JsonException">The request or response cannot be represented as protocol JSON.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="NotSupportedException">A requested response model or protocol value has no supported serializer.</exception>
    /// <exception cref="ArgumentException">An HTTP GET parameter is null or is not a scalar JSON value.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider or configured signer fails while verifying relay evidence or authenticating the request.</exception>
    public async Task SendHttpAsync(HttpMethod method, string name, ProtocolModel? parameters = null, bool authenticated = true, CancellationToken cancellationToken = default)
    {
        var json = await SendAuthenticatedHttpAsync(method, name, parameters, authenticated, cancellationToken).ConfigureAwait(false);
        if (json is not null)
            throw new InvalidDataException("A method without a result must return HTTP 204.");
    }

    async Task<string?> SendAuthenticatedHttpAsync(HttpMethod method, string name, ProtocolModel? parameters, bool authenticated, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var identity = authenticated ? GetAuthenticationIdentity() : null;
        await GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var endpoint = _httpEndpoint!;
        var session = identity is not null ? await GetHttpSessionAsync(endpoint, identity, linked.Token).ConfigureAwait(false) : null;
        try
        {
            return await SendHttpAsync(endpoint, method, name, parameters, session?.Credentials.Token, linked.Token).ConfigureAwait(false);
        }
        catch (RelayException exception) when (exception.Error.Code == "unauthorized")
        {
            if (ReferenceEquals(_httpSession, session))
                _httpSession = null;
            throw;
        }
    }

    async Task<Session> GetHttpSessionAsync(Uri endpoint, AuthenticationIdentity identity, CancellationToken cancellationToken)
    {
        Task<Session> authentication;
        await _httpAuthenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var origin = GetOrigin(endpoint);
            if (_httpSession is { } session && session.Origin == origin && session.RemainingSeconds > Math.Min(5, session.LifetimeSeconds * 0.2))
                return session;
            if (_httpAuthentication is not { IsCompleted: false } || _httpAuthenticationOrigin != origin)
            {
                _httpAuthenticationOrigin = origin;
                _httpAuthentication = RefreshHttpSessionAsync(endpoint, identity);
                _ = _httpAuthentication.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            authentication = _httpAuthentication;
        }
        finally
        {
            _httpAuthenticationGate.Release();
        }
        return await authentication.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task<Session> RefreshHttpSessionAsync(Uri endpoint, AuthenticationIdentity identity)
    {
        await Task.Yield();
        var session = await AuthenticateAsync(endpoint, (name, parameters, token) => SendHttpAsync(endpoint, HttpMethod.Post, name, parameters, null, token), identity, _lifetime.Token).ConfigureAwait(false);
        _httpSession = session;
        UpdateState(RelayConnectionState.Connected, AuthenticatedState);
        return session;
    }

    async Task<Session> AuthenticateAsync(Uri endpoint, Func<string, ProtocolModel, CancellationToken, Task<string?>> send, AuthenticationIdentity identity, CancellationToken cancellationToken)
    {
        if (!HasValidSession)
            UpdateState(authenticationState: RelayAuthenticationState.Authenticating);
        try
        {
            return await CreateSessionAsync(endpoint, send, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateState(authenticationState: HasValidSession ? AuthenticatedState : RelayAuthenticationState.None);
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
            throw;
        }
    }

    async Task<Session> CreateSessionAsync(Uri endpoint, Func<string, ProtocolModel, CancellationToken, Task<string?>> send, AuthenticationIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.AccountSigner is { } accountSigner && accountSigner.AccountId != identity.AccountId)
            throw new InvalidOperationException("The relay client's account identity has changed.");
        var certificate = identity.DeviceSigner?.Certificate;
        if (certificate is not null && (certificate.Account != identity.AccountId || certificate.GetDeviceId(_registry.Context) != identity.DeviceId))
            throw new InvalidOperationException("The relay client's device identity has changed.");
        var account = identity.AccountId;
        var started = Clock.GetTimestamp();
        var challenge = ReadModel<AuthenticationChallenge>(await send("auth.challenge", new AccountQuery { Account = account }, cancellationToken).ConfigureAwait(false));
        if (challenge.Validate() is { } violation)
            throw new InvalidDataException(violation.Message);
        var origin = GetOrigin(endpoint);
        ProtocolModel proof;
        string method;
        SessionMode mode;
        if (certificate is not null)
        {
            var request = new DeviceAuthenticationRequest { Nonce = challenge.Nonce, Timestamp = Clock.GetUtcNow().ToUnixTimeSeconds(), SignerCertificate = certificate, DeviceSignature = [] };
            var signature = await identity.DeviceSigner!.SignAsync(request.GetSigningInput(RelayId, origin, _registry.Context), cancellationToken).ConfigureAwait(false);
            proof = request with { DeviceSignature = [.. signature] };
            method = "auth.device.verify";
            mode = Models.Protocol.SessionMode.Device;
        }
        else
        {
            var signer = identity.AccountSigner!;
            var request = new AccountAuthenticationRequest { Nonce = challenge.Nonce, AccountPublicKey = signer.PublicKey, AccountSignature = [] };
            var signature = await signer.SignAsync(request.GetSigningInput(account, RelayId, origin, _registry.Context), cancellationToken).ConfigureAwait(false);
            proof = request with { AccountSignature = [.. signature] };
            method = "auth.account.verify";
            mode = Models.Protocol.SessionMode.Account;
        }
        if (Clock.GetElapsedTime(started).TotalSeconds >= challenge.ExpiresAt - challenge.CreatedAt)
            throw new InvalidOperationException("The authentication challenge expired before the proof could be submitted.");
        var credentials = ReadModel<SessionCredentials>(await send(method, proof, cancellationToken).ConfigureAwait(false));
        if (credentials.Validate() is { } credentialsViolation)
            throw new InvalidDataException(credentialsViolation.Message);
        var session = new Session(credentials, origin, started, credentials.ExpiresAt - (double)challenge.CreatedAt - 1, Clock);
        if (credentials.Mode != mode || session.RemainingSeconds <= 0)
            throw new InvalidDataException("The relay returned an expired session or an unexpected authentication mode.");
        return session;
    }

    async Task<string?> SendHttpAsync(Uri endpoint, HttpMethod method, string name, ProtocolModel? parameters, string? token, CancellationToken cancellationToken)
    {
        if (ConnectionState == RelayConnectionState.Disconnected)
            UpdateState(RelayConnectionState.Connecting);
        try
        {
            var result = await SendHttpCoreAsync(endpoint, method, name, parameters, token, cancellationToken).ConfigureAwait(false);
            UpdateState(RelayConnectionState.Connected);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ConnectionState == RelayConnectionState.Connecting)
                UpdateState(RelayConnectionState.Disconnected);
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
            throw;
        }
    }

    async Task<string?> SendHttpCoreAsync(Uri endpoint, HttpMethod method, string name, ProtocolModel? parameters, string? token, CancellationToken cancellationToken)
    {
        await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);
        var address = endpoint.AbsoluteUri.TrimEnd('/') + "/" + name.Replace('.', '/');
        string? body = parameters?.ToJson();
        if (method == HttpMethod.Get && body is not null)
        {
            using var document = JsonDocument.Parse(body);
            address += "?" + string.Join("&", document.RootElement.EnumerateObject().Select(static property =>
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                    _ => throw new ArgumentException("HTTP GET parameters must be non-null scalar values.")
                };
                return Uri.EscapeDataString(property.Name) + "=" + Uri.EscapeDataString(value);
            }));
            body = null;
        }
        using var request = new HttpRequestMessage(method, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
            request.Headers.Add("X-Meshline-Session", token);
        if (body is not null)
            request.Content = new StringContent(body, Utf8, "application/json");
        using var deadline = new RequestDeadline("relay.http." + name, TimeSpan.FromSeconds(60), cancellationToken, Clock);
        return await deadline.RunAsync(async requestToken =>
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            UpdateState(RelayConnectionState.Connected);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return null;
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("Relay redirects are not permitted.", null, response.StatusCode);
            var contentType = response.Content.Headers.ContentType;
            if (contentType is null || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Parameters.Any(static parameter => parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(parameter.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Relay responses must use application/json with UTF-8 encoding.");
            var bytes = await response.Content.ReadAsByteArrayAsync(requestToken).ConfigureAwait(false);
            var json = Utf8.GetString(bytes);
            if (!response.IsSuccessStatusCode)
            {
                var error = ReadModel<RelayError>(json);
                if (error.Code == "rate_limited")
                {
                    var retryAfter = error.GetRetryAfter();
                    var hasHeader = response.Headers.TryGetValues("Retry-After", out var values);
                    if (hasHeader && (retryAfter is null || values!.ToArray() is not [var value] || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds != retryAfter)
                        || response.StatusCode == HttpStatusCode.TooManyRequests && retryAfter is not null && !hasHeader)
                        throw new InvalidDataException("The Retry-After header must match the relay error retry_after value.");
                }
                ApplyRateLimit(error);
                throw new RelayException(error);
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("A method returning a result must return HTTP 200.");
            return json;
        }).ConfigureAwait(false);
    }

    void ApplyRateLimit(RelayError error)
    {
        if (error.Code != "rate_limited")
            return;
        var seconds = Math.Max(1, error.GetRetryAfter() ?? 1);
        lock (_backoffLock)
        {
            var remaining = _backoffSeconds - Clock.GetElapsedTime(_backoffStarted).TotalSeconds;
            _backoffSeconds = Math.Max(remaining, seconds);
            _backoffStarted = Clock.GetTimestamp();
        }
    }

    async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            double remaining;
            lock (_backoffLock)
                remaining = _backoffSeconds - Clock.GetElapsedTime(_backoffStarted).TotalSeconds;
            if (remaining <= 0)
                return;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(remaining, 60)), Clock, cancellationToken).ConfigureAwait(false);
        }
    }

    static T ReadModel<T>(string? json) where T : ProtocolModel =>
        json is null ? throw new InvalidDataException("The relay response has no result.")
            : ProtocolModel.FromJson<T>(json) ?? throw new JsonException("The relay response must contain an object.");

    static string GetOrigin(Uri endpoint)
    {
        var host = endpoint.HostNameType == UriHostNameType.IPv6 ? "[" + FormatIpv6(IPAddress.Parse(endpoint.DnsSafeHost)) + "]"
            : endpoint.IdnHost.ToLowerInvariant();
        return "https://" + host + (endpoint.Port == 443 ? "" : ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture));
    }

    static string FormatIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var groups = Enumerable.Range(0, 8).Select(index => bytes[index * 2] << 8 | bytes[index * 2 + 1]).ToArray();
        int start = -1, length = 0;
        for (var index = 0; index < groups.Length; index++)
        {
            if (groups[index] != 0)
                continue;
            var end = index;
            while (end < groups.Length && groups[end] == 0)
                end++;
            if (end - index > length && end - index >= 2)
            {
                start = index;
                length = end - index;
            }
            index = end - 1;
        }
        var text = groups.Select(static value => value.ToString("x", CultureInfo.InvariantCulture)).ToArray();
        return start < 0 ? string.Join(':', text) : string.Join(':', text[..start]) + "::" + string.Join(':', text[(start + length)..]);
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    bool HasValidSession => _httpSession is { RemainingSeconds: > 0 }
        || _socket is { Authentication.RemainingSeconds: > 0, Lifetime.IsCancellationRequested: false };

    RelayAuthenticationState AuthenticatedState => SessionMode switch
    {
        Models.Protocol.SessionMode.Account => RelayAuthenticationState.Account,
        Models.Protocol.SessionMode.Device => RelayAuthenticationState.Device,
        _ => RelayAuthenticationState.None
    };

    void UpdateState(RelayConnectionState? connectionState = null, RelayAuthenticationState? authenticationState = null, Exception? error = null, SessionMode? sessionMode = null)
    {
        lock (_stateGate)
        {
            var connection = connectionState ?? _connectionState;
            var authentication = authenticationState ?? _authenticationState;
            var mode = sessionMode ?? _sessionMode;
            if (Volatile.Read(ref _disposed) != 0)
            {
                connection = RelayConnectionState.Disconnected;
                authentication = RelayAuthenticationState.None;
                mode = _sessionMode;
                error = _lastError;
            }
            if (_connectionState == connection && _authenticationState == authentication && _sessionMode == mode && ReferenceEquals(_lastError, error))
                return;
            _connectionState = connection;
            _authenticationState = authentication;
            _sessionMode = mode;
            _lastError = error;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    void ReportFailure(Exception exception)
    {
        var disconnected = exception is HttpRequestException { StatusCode: null } or System.Net.WebSockets.WebSocketException or IOException or OperationCanceledException or TimeoutException;
        var authentication = exception is RelayException { Error.Code: "unauthorized" or "device_unknown" or "invalid_signature" }
            ? RelayAuthenticationState.Rejected
            : _authenticationState == RelayAuthenticationState.Authenticating ? HasValidSession ? AuthenticatedState : RelayAuthenticationState.None : AuthenticationState;
        UpdateState(disconnected ? RelayConnectionState.Disconnected : null, authentication, exception);
    }

    internal async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _authenticationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisposeSocketAsync().ConfigureAwait(false);
                if (_httpAuthentication is { } authentication)
                {
                    try { await authentication.ConfigureAwait(false); }
                    catch (Exception) { /* Authentication failures are observed by request callers. */ }
                }
                _httpSession = null;
                _identity = null;
            }
            finally
            {
                _authenticationGate.Release();
            }
        }
        finally
        {
            try
            {
                Task? notifications;
                lock (_notificationGate)
                    notifications = _notifications;
                if (notifications is not null)
                    await notifications.ConfigureAwait(false);
            }
            finally
            {
                UpdateState(RelayConnectionState.Disconnected, RelayAuthenticationState.None);
            }
        }
    }

    sealed record AuthenticationIdentity
    {
        public SessionMode Mode => DeviceSigner is null ? Models.Protocol.SessionMode.Account : Models.Protocol.SessionMode.Device;
        public IAccountSigner? AccountSigner { get; }
        public IDeviceSigner? DeviceSigner { get; }
        public string AccountId { get; }
        public string? DeviceId { get; }

        public AuthenticationIdentity(IAccountSigner accountSigner)
        {
            AccountSigner = accountSigner;
            AccountId = accountSigner.AccountId;
        }

        public AuthenticationIdentity(IDeviceSigner deviceSigner, NetworkContext context)
        {
            var certificate = deviceSigner.Certificate;
            DeviceSigner = deviceSigner;
            AccountId = certificate.Account;
            DeviceId = certificate.GetDeviceId(context);
        }
    }

    sealed record Session(SessionCredentials Credentials, string Origin, long Started, double LifetimeSeconds, TimeProvider Clock)
    {
        public double RemainingSeconds => LifetimeSeconds - Clock.GetElapsedTime(Started).TotalSeconds;
    }
}
