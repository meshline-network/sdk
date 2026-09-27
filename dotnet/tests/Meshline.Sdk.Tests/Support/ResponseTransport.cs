using Meshline.Models.Protocol;
using Meshline.Transport;

namespace Meshline.Tests.Support;

internal static class ResponseTransport
{
    // The caller owns the relay, client pool, clock scope and assertions.
    internal static Task<T> SendAsync<T>(
        RelayClient client,
        OfflineRelay relay,
        bool webSocket,
        HttpMethod verb,
        string method,
        string json,
        Action<RpcRequest> inspectRequest,
        CancellationToken cancellationToken)
        where T : ProtocolModel
    {
        relay.Handler = (request, _) => Task.FromResult(request.Method == method ? OfflineRelay.Json(json) : relay.Respond(request));
        relay.SocketHandler = (request, socket) =>
        {
            inspectRequest(request);
            socket.Reply(request.Id!, json);
            return Task.CompletedTask;
        };

        return webSocket
            ? client.SendWebSocketAsync<T>(method, cancellationToken: cancellationToken)
            : client.SendHttpAsync<T>(verb, method, cancellationToken: cancellationToken);
    }
}
