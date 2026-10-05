using Meshline.Models.Protocol;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Meshline.Transport;

sealed partial class RelayClient
{
    // Cancellation stops new attempts immediately. The caller completes requests
    // after its producers stop; completion gives an in-flight request and the
    // final clear a shared five-second budget. Await this task to finish cleanup.
    internal async Task RunSubscriptionAsync(string method, ProtocolModel empty, ChannelReader<ProtocolModel> requests,
        Action subscribed, Action<Exception> reportError, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var pending = new CancellationTokenSource(Timeout.InfiniteTimeSpan, Clock);
        var token = stopping.Token;
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var gate = new Lock();
        ProtocolModel? desired = null;
        SocketSession? connection = null;
        string? appliedJson = null;
        var emptyJson = empty.ToJson();
        SocketConnected += OnConnected;
        var updates = ReadUpdatesAsync();
        try
        {
            while (await wake.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (wake.Reader.TryRead(out _)) { }
                try
                {
                    var socket = await WaitForSocketAsync(token).ConfigureAwait(false);
                    ProtocolModel? request;
                    // Read the latest set after readiness; updates can arrive
                    // while this relay is unavailable.
                    lock (gate) request = desired;
                    if (request is null) continue;
                    var json = request.ToJson();
                    if (json == appliedJson && ReferenceEquals(socket, connection)) continue;
                    // A lost replacement response invalidates the previous ACK.
                    appliedJson = null;
                    connection = socket;
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await SendSubscriptionAsync(socket, method, request, pending.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception) when (pending.IsCancellationRequested)
                    {
                        // A cleanup timeout remains visible after component events detach.
                        reportError(exception);
                        throw;
                    }
                    token.ThrowIfCancellationRequested();
                    appliedJson = json;
                    if (json != emptyJson) subscribed();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    reportError(exception);
                    if (exception is HttpRequestException or WebSocketException or IOException or OperationCanceledException or TimeoutException
                        || exception is RelayException { Error.Code: "unauthorized" or "temporarily_unavailable" or "bad_gateway" or "rate_limited" })
                    {
                        // Retry empty sets too: after removing the last resource,
                        // the component may no longer submit updates for this relay.
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), Clock);
                        using var retry = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                        try { await wake.Reader.WaitToReadAsync(retry.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                        {
                            wake.Writer.TryWrite(true);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            SocketConnected -= OnConnected;
            try { await updates.ConfigureAwait(false); }
            finally
            {
                if (connection is { Failure: null })
                {
                    try { await SendSubscriptionAsync(connection, method, empty, pending.Token).ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        // Even a rejected clear leaves the set active. Retire
                        // this exact connection, never a subsequent replacement.
                        connection.Stop(new IOException("The relay subscriptions could not be cleared; reconnect to discard the old subscription set.", exception));
                        reportError(exception);
                    }
                }
            }
        }

        void OnConnected(object? sender, EventArgs args) => wake.Writer.TryWrite(true);

        async Task ReadUpdatesAsync()
        {
            try
            {
                // Keep draining until the producer completes, including after
                // runtime cancellation. This separates quiescing from cleanup.
                await foreach (var request in requests.ReadAllAsync().ConfigureAwait(false))
                {
                    lock (gate) desired = request;
                    wake.Writer.TryWrite(true);
                }
            }
            finally
            {
                pending.CancelAfter(TimeSpan.FromSeconds(5));
                await stopping.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    async Task SendSubscriptionAsync(SocketSession socket, string method, ProtocolModel request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (await SendSocketRequestAsync(socket, method, request, token).ConfigureAwait(false) is { } result && result != "null")
                throw new InvalidDataException("A subscription method must return JSON null.");
        }
        catch (Exception exception) when (exception is not RelayException)
        {
            // An unknown outcome must be resolved before replacing the set.
            socket.Stop(new IOException("The relay subscription outcome is unknown; reconnect before replacing subscriptions.", exception));
            throw;
        }
    }
}
