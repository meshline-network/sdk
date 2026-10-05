using Meshline.Models.Protocol;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Meshline.Transport;

sealed partial class RelayClient
{
    const int MaxSocketMessageBytes = 1_048_576;
    readonly SemaphoreSlim _socketGate = new(1, 1);
    readonly Lock _notificationGate = new();
    SocketSession? _socket;
    Task? _notifications;
    TaskCompletionSource<SocketSession> _socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Occurs when a JSON-RPC notification is dispatched from the relay WebSocket stream.
    /// </summary>
    public event EventHandler<RpcRequest>? NotificationReceived;
    /// <summary>
    /// Occurs after a WebSocket notification stream connects or reconnects and authenticates.
    /// </summary>
    public event EventHandler? SocketConnected;
    /// <summary>
    /// Occurs when the notification dispatcher reports an error or connection invalidation.
    /// </summary>
    public event EventHandler<Exception>? ErrorOccurred;
    /// <summary>
    /// Occurs when the notification dispatcher encounters a terminal failure that retires this client from its pool.
    /// </summary>
    public event EventHandler? Faulted;

    internal void StartNotifications()
    {
        lock (_notificationGate)
        {
            ThrowIfDisposed();
            if (SessionMode != Models.Protocol.SessionMode.Device)
                throw new InvalidOperationException("Relay notifications require a device session.");
            _ = GetAuthenticationIdentity();
            _notifications ??= DispatchNotificationsAsync(_lifetime.Token);
        }
    }

    async Task<SocketSession> WaitForSocketAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartNotifications();
        Task<SocketSession> ready;
        lock (_notificationGate) ready = _socketReady.Task;
        var socket = await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return socket;
    }

    async Task DispatchNotificationsAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var failures = 0;
        var terminal = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var socket = await GetSocketAsync(cancellationToken).ConfigureAwait(false);
                    SocketConnected?.Invoke(this, EventArgs.Empty);
                    lock (_notificationGate)
                        _socketReady.TrySetResult(socket);
                    failures = 0;
                    await foreach (var notification in socket.Notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        NotificationReceived?.Invoke(this, notification);
                    throw new IOException("The relay notification stream ended.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    // A completed channel can deliver its original failure while disposal
                    // cancels the dispatcher. Report that failure before ending the loop.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        ErrorOccurred?.Invoke(this, exception);
                        return;
                    }
                    lock (_notificationGate)
                        if (_socketReady.Task.IsCompleted)
                            _socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var retry = exception is HttpRequestException or WebSocketException or OperationCanceledException or TimeoutException
                        || exception is IOException
                        || exception is RelayException { Error.Code: "unauthorized" or "temporarily_unavailable" or "bad_gateway" or "rate_limited" };
                    ErrorOccurred?.Invoke(this, exception);
                    if (!retry)
                    {
                        terminal = true;
                        Faulted?.Invoke(this, EventArgs.Empty);
                        lock (_notificationGate)
                        {
                            _socketReady.TrySetException(exception);
                            _ = _socketReady.Task.Exception;
                        }
                        return;
                    }
                    var seconds = Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))) + Random.Shared.NextDouble();
                    await Task.Delay(TimeSpan.FromSeconds(seconds), Clock, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_notificationGate)
                _socketReady.TrySetCanceled(cancellationToken);
            if (cancellationToken.IsCancellationRequested && !terminal)
                ErrorOccurred?.Invoke(this, new InvalidOperationException("The relay connection was disposed or invalidated."));
        }
    }

    /// <summary>
    /// Sends a JSON-RPC request through an authenticated WebSocket session and awaits its response.
    /// </summary>
    /// <param name="method">The JSON-RPC method name to invoke.</param>
    /// <param name="parameters">Optional named protocol parameters, serialized according to the transport operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <typeparam name="T">The protocol model type expected in the response.</typeparam>
    /// <returns>A task whose result is the deserialized response model after its protocol validation succeeds.</returns>
    /// <remarks>Account and device sessions can send requests. Device sessions also start notification dispatch; reconnection does not replay the request. Waiting for connection readiness shares the request's 60-second timeout.</remarks>
    /// <exception cref="InvalidOperationException">The relay is not registered as active, or the authentication identity required by the request is unavailable or has changed.</exception>
    /// <exception cref="NotSupportedException">The relay has no WebSocket endpoint, or a protocol value has no supported serializer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured HTTP or JSON-RPC protocol error.</exception>
    /// <exception cref="InvalidDataException">The response is missing, violates transport or model rules, or contains inconsistent relay evidence.</exception>
    /// <exception cref="JsonException">The request or response cannot be represented as protocol JSON.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">This relay client, its pool, or its supplied HTTP client has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="WebSocketException">The WebSocket handshake, send, or receive operation fails.</exception>
    /// <exception cref="IOException">The WebSocket stream closes before the pending response is received.</exception>
    /// <exception cref="ArgumentException">The serialized WebSocket request exceeds 1 MiB.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider or configured signer fails while verifying relay evidence or authenticating the request.</exception>
    public async Task<T> SendWebSocketAsync<T>(string method, ProtocolModel? parameters = null, CancellationToken cancellationToken = default) where T : ProtocolModel
    {
        var model = ReadModel<T>(await SendWebSocketCoreAsync(method, parameters, cancellationToken).ConfigureAwait(false));
        if (model.Validate(_registry.Context) is { } violation)
            throw new InvalidDataException(violation.Message);
        return model;
    }

    /// <summary>
    /// Sends a JSON-RPC request through an authenticated WebSocket session and awaits its response.
    /// </summary>
    /// <param name="method">The JSON-RPC method name to invoke.</param>
    /// <param name="parameters">Optional named protocol parameters, serialized according to the transport operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>The successful JSON-RPC result must be null. Device sessions also start notification dispatch; reconnection does not replay the request. Waiting for connection readiness shares the request's 60-second timeout.</remarks>
    /// <exception cref="InvalidOperationException">The relay is not registered as active, or the authentication identity required by the request is unavailable or has changed.</exception>
    /// <exception cref="NotSupportedException">The relay has no WebSocket endpoint, or a protocol value has no supported serializer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured HTTP or JSON-RPC protocol error.</exception>
    /// <exception cref="InvalidDataException">The response is missing, violates transport or model rules, or contains inconsistent relay evidence.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="ObjectDisposedException">This relay client, its pool, or its supplied HTTP client has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="JsonException">The request or response cannot be represented as protocol JSON.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="WebSocketException">The WebSocket handshake, send, or receive operation fails.</exception>
    /// <exception cref="IOException">The WebSocket stream closes before the pending response is received.</exception>
    /// <exception cref="ArgumentException">The serialized WebSocket request exceeds 1 MiB.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider or configured signer fails while verifying relay evidence or authenticating the request.</exception>
    public async Task SendWebSocketAsync(string method, ProtocolModel? parameters = null, CancellationToken cancellationToken = default)
    {
        if (await SendWebSocketCoreAsync(method, parameters, cancellationToken).ConfigureAwait(false) is { } result && result != "null")
            throw new InvalidDataException("A method without a result must return JSON null.");
    }

    async Task<string?> SendWebSocketCoreAsync(string method, ProtocolModel? parameters, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var deadline = new RequestDeadline("relay.websocket." + method, TimeSpan.FromSeconds(60), linked.Token, Clock);
        return await deadline.RunAsync(async requestToken =>
        {
            var socket = SessionMode == Models.Protocol.SessionMode.Device
                ? await WaitForSocketAsync(requestToken).ConfigureAwait(false)
                : await GetSocketAsync(requestToken).ConfigureAwait(false);
            return await SendSocketRequestAsync(socket, method, parameters, requestToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    Task<SocketSession> GetSocketAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return GetSocketAsync(GetAuthenticationIdentity(), cancellationToken);
    }

    async Task<SocketSession> GetSocketAsync(AuthenticationIdentity identity, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var descriptor = await GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _socketGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var endpoint = descriptor.Endpoints.FirstOrDefault(static value => value.StartsWith("wss://", StringComparison.Ordinal))
                ?? throw new NotSupportedException("This relay does not advertise a WebSocket endpoint.");
            if (_socket is { } existing && existing.Endpoint == new Uri(endpoint) && !existing.Lifetime.IsCancellationRequested && existing.Authentication is { RemainingSeconds: > 0 })
                return existing;
            if (_socket is { } previous)
            {
                previous.Stop(new WebSocketException("The previous relay session is no longer usable."));
                await ReleaseSocketAsync(previous).ConfigureAwait(false);
            }
            var socket = new SocketSession(new(endpoint), _lifetime.Token, OnSocketStopped);
            _socket = socket;
            UpdateState(RelayConnectionState.Connecting);
            try
            {
                using var deadline = new RequestDeadline("relay.websocket.connect", TimeSpan.FromSeconds(60), linked.Token, Clock);
                socket.Authentication = await deadline.RunAsync(async requestToken =>
                {
                    await socket.Connection.ConnectAsync(socket.Endpoint, _http, requestToken).ConfigureAwait(false);
                    socket.ReceiveTask = ReceiveSocketAsync(socket);
                    return await AuthenticateAsync(socket.Endpoint, (name, parameters, token) => SendSocketRequestAsync(socket, name, parameters, token), identity, requestToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
                if (SessionMode is not null)
                    UpdateState(RelayConnectionState.Connected, AuthenticatedState);
                socket.RenewalTask = RenewSocketAsync(socket, identity);
                return socket;
            }
            catch (Exception exception)
            {
                socket.Stop(exception);
                await ReleaseSocketAsync(socket).ConfigureAwait(false);
                _socket = null;
                throw;
            }
        }
        finally
        {
            _socketGate.Release();
        }
    }

    // Sends on the supplied connection only; callers decide whether an uncertain
    // operation requires retiring that connection or reconciling remote state.
    async Task<string?> SendSocketRequestAsync(SocketSession socket, string method, ProtocolModel? parameters, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, socket.Lifetime.Token);
        using var deadline = new RequestDeadline("relay.websocket." + method, TimeSpan.FromSeconds(60), linked.Token, Clock);
        return await deadline.RunAsync(_ => SendSocketRequestCoreAsync(socket, method, parameters, deadline, cancellationToken)).ConfigureAwait(false);
    }

    async Task<string?> SendSocketRequestCoreAsync(SocketSession socket, string method, ProtocolModel? parameters, RequestDeadline deadline, CancellationToken callerCancellationToken)
    {
        var cancellationToken = deadline.Token;
        await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);
        ImmutableDictionary<string, JsonElement>? values = null;
        if (parameters is not null)
        {
            using var document = JsonDocument.Parse(parameters.ToJson());
            values = document.RootElement.EnumerateObject().ToImmutableDictionary(static property => property.Name, static property => property.Value.Clone(), StringComparer.Ordinal);
        }
        var id = Guid.NewGuid().ToString("N");
        var request = new RpcRequest { Id = id, Method = method, Params = values };
        var bytes = Utf8.GetBytes(request.ToJson());
        if (bytes.Length > MaxSocketMessageBytes)
            throw new ArgumentException("A WebSocket request cannot exceed 1 MiB.", nameof(parameters));
        var pending = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (method is "auth.device.verify" or "auth.account.verify")
        {
            socket.AuthenticationRequestId = id;
            socket.ExpectedMode = method == "auth.device.verify" ? Models.Protocol.SessionMode.Device : Models.Protocol.SessionMode.Account;
        }
        socket.Pending.TryAdd(id, pending);
        try
        {
            await socket.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.Failure is { } failure)
                    throw new WebSocketException("The relay connection has failed.", failure);
                await socket.Connection.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var failure = deadline.Classify(exception);
                socket.Stop(failure);
                if (ReferenceEquals(failure, exception)) throw;
                throw failure;
            }
            finally
            {
                socket.SendGate.Release();
            }
            var response = await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response is RpcFailure error)
            {
                var code = error.Error.GetRelayErrorCode()
                    ?? throw new InvalidDataException($"The relay returned JSON-RPC error {error.Error.Code}: {error.Error.Message}");
                var relayError = new RelayError { Code = code, Message = error.Error.Message, Data = error.Error.Data };
                ApplyRateLimit(relayError);
                if (code == "unauthorized")
                    socket.Stop(new RelayException(relayError));
                throw new RelayException(relayError);
            }
            UpdateState(RelayConnectionState.Connected);
            return ((RpcSuccess)response).Result?.GetRawText();
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (socket.Failure is { } failure)
            {
                ReportFailure(failure);
                ExceptionDispatchInfo.Throw(failure);
            }
            ReportFailure(exception);
            throw;
        }
        finally
        {
            socket.Pending.TryRemove(id, out _);
            if (pending.Task.IsFaulted)
                _ = pending.Task.Exception;
        }
    }

    async Task ReceiveSocketAsync(SocketSession socket)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!socket.Lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.Connection.ReceiveAsync(buffer.AsMemory(), socket.Lifetime.Token).ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("The relay closed the WebSocket connection.");
                    if (frame.MessageType != WebSocketMessageType.Text)
                    {
                        await RejectSocketAsync(socket, WebSocketCloseStatus.InvalidMessageType, "Only text messages are supported.").ConfigureAwait(false);
                        throw new InvalidDataException("The relay sent a binary WebSocket message.");
                    }
                    if (message.Length + frame.Count > MaxSocketMessageBytes)
                    {
                        await RejectSocketAsync(socket, WebSocketCloseStatus.MessageTooBig, "The message exceeds 1 MiB.").ConfigureAwait(false);
                        throw new InvalidDataException("The relay WebSocket message exceeds 1 MiB.");
                    }
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                var json = Utf8.GetString(message.GetBuffer().AsSpan(0, checked((int)message.Length)));
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("A relay WebSocket message must contain one JSON object.");
                if (!document.RootElement.TryGetProperty("id", out _))
                {
                    var notification = ReadModel<RpcRequest>(json);
                    if (string.IsNullOrEmpty(notification.Method))
                        throw new JsonException("A relay notification must have a method and no id.");
                    if (socket.EstablishedMode != Models.Protocol.SessionMode.Device)
                        throw new InvalidDataException("Only an authenticated device session can receive notifications.");
                    if (!socket.Notifications.Writer.TryWrite(notification))
                        throw new IOException("The relay notification queue is full; reconnect and synchronize to recover missed updates.");
                }
                else
                {
                    var response = ReadModel<RpcResponse>(json);
                    var id = response is RpcSuccess success ? success.Id : ((RpcFailure)response).Id;
                    if (id is null)
                        throw new InvalidDataException("The relay response cannot be correlated with a request.");
                    if (id == socket.AuthenticationRequestId && response is RpcSuccess authenticated)
                    {
                        var credentials = ReadModel<SessionCredentials>(authenticated.Result?.GetRawText());
                        if (credentials.Validate() is { } violation)
                            throw new InvalidDataException(violation.Message);
                        if (credentials.Mode != socket.ExpectedMode)
                            throw new InvalidDataException("The relay returned an unexpected authentication mode.");
                        socket.EstablishedMode = credentials.Mode;
                    }
                    if (socket.Pending.TryRemove(id, out var pending))
                        pending.TrySetResult(response);
                }
            }
        }
        catch (Exception exception)
        {
            socket.Stop(exception);
        }
        finally
        {
            socket.Connection.Dispose();
        }
    }

    async Task RejectSocketAsync(SocketSession socket, WebSocketCloseStatus status, string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3), Clock);
        await socket.SendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await socket.Connection.CloseOutputAsync(status, description, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            socket.SendGate.Release();
        }
    }

    async Task RenewSocketAsync(SocketSession socket, AuthenticationIdentity identity)
    {
        try
        {
            while (!socket.Lifetime.IsCancellationRequested)
            {
                var session = socket.Authentication!;
                var remaining = session.RemainingSeconds;
                if (remaining <= 0)
                    throw new WebSocketException("The relay WebSocket session expired.");
                var renewIn = Math.Max(remaining * 0.8, remaining - 30);
                if (renewIn > 3600)
                {
                    await Task.Delay(TimeSpan.FromHours(1), Clock, socket.Lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(renewIn), Clock, socket.Lifetime.Token).ConfigureAwait(false);
                try
                {
                    var renewed = await AuthenticateAsync(socket.Endpoint, (name, parameters, token) => SendSocketRequestAsync(socket, name, parameters, token), identity, socket.Lifetime.Token).ConfigureAwait(false);
                    if (session.RemainingSeconds <= 0)
                        throw new WebSocketException("The previous WebSocket session expired before renewal completed.");
                    socket.Authentication = renewed;
                    UpdateState(RelayConnectionState.Connected, AuthenticatedState);
                }
                catch (RelayException exception) when (exception.Error.Code != "unauthorized" && session.RemainingSeconds > 0)
                {
                    UpdateState(RelayConnectionState.Connected, AuthenticatedState, exception);
                    ErrorOccurred?.Invoke(this, exception);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, session.RemainingSeconds)), Clock, socket.Lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            socket.Stop(exception);
        }
    }

    async ValueTask DisposeSocketAsync()
    {
        SocketSession? socket;
        await _socketGate.WaitAsync().ConfigureAwait(false);
        try
        {
            socket = _socket;
            _socket = null;
            socket?.Stop(new ObjectDisposedException(nameof(RelayClient)));
        }
        finally
        {
            _socketGate.Release();
        }
        if (socket is not null)
            await ReleaseSocketAsync(socket).ConfigureAwait(false);
    }

    static async Task ReleaseSocketAsync(SocketSession socket)
    {
        await Task.WhenAll(socket.ReceiveTask, socket.RenewalTask).ConfigureAwait(false);
        socket.Connection.Dispose();
        socket.Lifetime.Dispose();
    }

    void OnSocketStopped(SocketSession socket, Exception error)
    {
        // A request can retire the socket before the dispatcher observes its
        // failure. Retries must wait for the next connection's announcement.
        lock (_notificationGate)
        {
            if (!ReferenceEquals(_socket, socket))
                return;
            if (_socketReady.Task.IsCompletedSuccessfully)
                _socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        UpdateState(RelayConnectionState.Disconnected, error is RelayException { Error.Code: "unauthorized" or "device_unknown" or "invalid_signature" }
            ? RelayAuthenticationState.Rejected : AuthenticationState, error);
    }

    sealed class SocketSession(Uri endpoint, CancellationToken cancellationToken, Action<SocketSession, Exception> stopped)
    {
        public Uri Endpoint { get; } = endpoint;
        public ClientWebSocket Connection { get; } = new();
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public ConcurrentDictionary<string, TaskCompletionSource<RpcResponse>> Pending { get; } = new(StringComparer.Ordinal);
        public Channel<RpcRequest> Notifications { get; } = Channel.CreateBounded<RpcRequest>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });
        public Session? Authentication { get; set; }
        public string? AuthenticationRequestId { get; set; }
        public SessionMode ExpectedMode { get; set; }
        public SessionMode? EstablishedMode { get; set; }
        public Exception? Failure => _failure;
        public Task ReceiveTask { get; set; } = Task.CompletedTask;
        public Task RenewalTask { get; set; } = Task.CompletedTask;
        Exception? _failure;

        public void Stop(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _failure, exception, null) is not null)
                return;
            Lifetime.Cancel();
            Connection.Abort();
            foreach (var (id, pending) in Pending)
                if (Pending.TryRemove(id, out _))
                    pending.TrySetException(exception);
            Notifications.Writer.TryComplete(exception);
            stopped(this, exception);
        }
    }
}
