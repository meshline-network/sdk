using Meshline.Models.Protocol;
using Meshline.Transport;
using System.Threading.Channels;

namespace Meshline.Components;

// Submit the latest desired set independently for each relay. Connection state
// and the complete subscription lifecycle belong to RelayClient.
sealed class RelaySubscriptions(
    string method,
    ProtocolModel empty,
    Action<RelayClient, Exception> reportError,
    Action subscribed,
    CancellationToken cancellationToken) : IAsyncDisposable
{
    // Retain empty sets until stop so unfollow/refollow updates stay ordered.
    readonly Dictionary<RelayClient, (Channel<ProtocolModel> Requests, Task Worker)> _subscriptions = [];

    // Called by the component's refresh loop, then drained after that loop stops.
    internal void Update(RelayClient relay, ProtocolModel request)
    {
        if (!_subscriptions.TryGetValue(relay, out var subscription))
        {
            var requests = Channel.CreateBounded<ProtocolModel>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
            var worker = relay.RunSubscriptionAsync(method, empty, requests.Reader, subscribed,
                error => reportError(relay, error), cancellationToken);
            _subscriptions.Add(relay, subscription = (requests, worker));
        }
        subscription.Requests.Writer.TryWrite(request);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions.Values) subscription.Requests.Writer.TryComplete();
        await Task.WhenAll(_subscriptions.Values.Select(value => value.Worker)).ConfigureAwait(false);
        _subscriptions.Clear();
    }
}
