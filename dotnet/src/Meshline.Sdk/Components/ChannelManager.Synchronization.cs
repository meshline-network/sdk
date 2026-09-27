using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;

namespace Meshline.Components;

sealed partial class ChannelManager
{
    readonly Lock _subscriptionsGate = new();
    readonly Dictionary<string, RelayClient> _subscribedRelays = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> _subscribedChannels = new(StringComparer.Ordinal);
    RelaySubscriptions? _subscriptionWorker;
    Channel<bool>? _refreshRequests;
    Task _refresh = Task.CompletedTask;
    Task _poll = Task.CompletedTask;

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No local device certificate is available for channel subscriptions.</exception>
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        _ = Certificate;
        _refreshRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        _subscriptionWorker = new("channel.subscribe", new ChannelSubscriptionRequest { ChannelIds = [] },
            (relay, error) => ReportBackgroundError(BackgroundOperation.Connect, relay.RelayId, error), QueueRefresh, RuntimeCancellationToken);
        _refresh = RefreshFollowedAsync(RuntimeCancellationToken);
        _poll = PollFollowedAsync(RuntimeCancellationToken);
        QueueRefresh();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task OnStopAsync()
    {
        await Task.WhenAll(_refresh, _poll).ConfigureAwait(false);
        RelayClient[] relays;
        lock (_subscriptionsGate)
        {
            relays = _subscribedRelays.Values.ToArray();
            _subscribedRelays.Clear();
            _subscribedChannels.Clear();
            _refreshRequests = null;
        }
        foreach (var relay in relays) DetachRelay(relay);
        if (_subscriptionWorker is not null) await _subscriptionWorker.DisposeAsync().ConfigureAwait(false);
        _subscriptionWorker = null;
    }

    void QueueRefresh()
    {
        lock (_subscriptionsGate) _refreshRequests?.Writer.TryWrite(true);
    }

    async Task PollFollowedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Clock.Provider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) QueueRefresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    async Task RefreshFollowedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reader = _refreshRequests!.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out _)) { }
                try
                {
                    await RecoverOperationsAsync(cancellationToken).ConfigureAwait(false);
                    List<ChannelRecord> followed;
                    await using (var database = new MeshlineDbContext(databaseOptions))
                        followed = await database.Channels.AsNoTracking().Where(value => value.IsFollowed).OrderBy(value => value.ChannelId).ToListAsync(cancellationToken).ConfigureAwait(false);
                    RemoveUnusedSubscriptions(followed.Select(value => value.RelayId).ToHashSet(StringComparer.Ordinal));
                    foreach (var group in followed.GroupBy(value => value.RelayId))
                    {
                        RelayClient relay;
                        try { relay = await GetHostingRelayAsync(group.Key, cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            ReportBackgroundError(BackgroundOperation.Connect, group.Key, exception);
                            continue;
                        }
                        try { await SubscribeAsync(relay, group.Select(value => value.ChannelId).ToArray(), cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Connect, relay.RelayId, exception); }
                        foreach (var record in group)
                        {
                            try
                            {
                                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                                try
                                {
                                    var channel = new ChannelRef { ChannelId = record.ChannelId, RelayId = record.RelayId };
                                    await SaveDescriptorAsync(channel, await ResolveDescriptorAsync(relay, channel, null, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                                    await SynchronizeAsync(relay, channel, record.SyncSequence, true, cancellationToken).ConfigureAwait(false);
                                }
                                finally { _writeGate.Release(); }
                            }
                            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, record.ChannelId, exception); }
                        }
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    ReportBackgroundError(BackgroundOperation.Synchronize, null, exception);
                    await Task.Delay(TimeSpan.FromSeconds(5), Clock.Provider, cancellationToken).ConfigureAwait(false);
                    QueueRefresh();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    async Task SynchronizeAsync(RelayClient relay, ChannelRef channel, long after, bool advance, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await ReadPageAsync(relay, channel, new ChannelReadQuery { ChannelId = channel.ChannelId, After = after }, advance, cancellationToken).ConfigureAwait(false);
            if (!page.HasMore) return;
            after = page.Events[^1].Sequence;
        }
    }

    async Task RecoverOperationsAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ChannelOperationRecord> operations;
            await using (var database = new MeshlineDbContext(databaseOptions))
                operations = await database.ChannelOperations.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var operation in operations)
            {
                try
                {
                    var request = ProtocolModel.FromJson<TypedProtocolModel>(operation.DocumentJson)!;
                    var relay = await GetHostingRelayAsync(operation.RelayId, cancellationToken).ConfigureAwait(false);
                    if (request is ChannelDescriptor descriptor)
                    {
                        var channel = new ChannelRef { ChannelId = descriptor.ChannelId, RelayId = operation.RelayId };
                        ProtocolModel payload = operation.Method == "channel.close"
                            ? new ChannelCloseRequest { ChannelId = descriptor.ChannelId, Revision = descriptor.Revision, UpdatedAt = descriptor.UpdatedAt, DeviceSignature = descriptor.DeviceSignature }
                            : descriptor;
                        await PublishDescriptorAsync(relay, channel, operation, payload, descriptor, recovering: true, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (request is ChannelPost publication)
                    {
                        var channel = new ChannelRef { ChannelId = publication.ChannelId, RelayId = operation.RelayId };
                        await using var database = new MeshlineDbContext(databaseOptions);
                        var after = await database.Channels.Where(value => value.ChannelId == channel.ChannelId).Select(value => (long?)value.SyncSequence).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? -1;
                        await SynchronizeAsync(relay, channel, after, false, cancellationToken).ConfigureAwait(false);
                        await PublishPostCoreAsync(relay, channel, operation, publication, recovering: true, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var (channelId, sequence) = request switch
                    {
                        Models.Protocol.ChannelPostEdit edit => (edit.ChannelId, edit.TargetSequence),
                        ChannelPostDelete deletion => (deletion.ChannelId, deletion.TargetSequence),
                        _ => throw new InvalidDataException("The stored channel operation has an unexpected payload.")
                    };
                    var post = new ChannelPostRef { Channel = new() { ChannelId = channelId, RelayId = operation.RelayId }, Sequence = sequence };
                    await SynchronizeAsync(relay, post.Channel, sequence - 1, false, cancellationToken).ConfigureAwait(false);
                    await using (var database = new MeshlineDbContext(databaseOptions))
                        if (!await database.ChannelOperations.AnyAsync(value => value.RelayId == operation.RelayId && value.ResourceId == operation.ResourceId && value.Method == operation.Method && value.DocumentJson == operation.DocumentJson, cancellationToken).ConfigureAwait(false)) continue;
                    await ApplyPostOperationAsync(relay, post, operation, request, recovering: true, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, operation.ResourceId, exception); }
            }
        }
        finally { _writeGate.Release(); }
    }

    async Task SubscribeAsync(RelayClient relay, string[] channels, CancellationToken cancellationToken)
    {
        var descriptor = await relay.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (!descriptor.Endpoints.Any(value => value.StartsWith("wss://", StringComparison.Ordinal))) return;
        var limits = (await relay.GetInfoAsync(cancellationToken).ConfigureAwait(false)).Limits;
        var selected = channels.Take((int)Math.Min(limits.MaxChannelSubscriptions ?? 0, int.MaxValue)).ToImmutableArray();
        lock (_subscriptionsGate)
        {
            if (!_subscribedRelays.TryGetValue(relay.RelayId, out var previous) || !ReferenceEquals(previous, relay))
            {
                if (previous is not null) DetachRelay(previous);
                _subscribedRelays[relay.RelayId] = relay;
                relay.NotificationReceived += OnChannelNotification;
                relay.SocketConnected += OnChannelSocketConnected;
                relay.ErrorOccurred += OnChannelConnectionError;
            }
            _subscribedChannels[relay.RelayId] = selected.ToHashSet(StringComparer.Ordinal);
        }
        _subscriptionWorker!.Update(relay, new ChannelSubscriptionRequest { ChannelIds = selected });
    }

    void RemoveUnusedSubscriptions(HashSet<string> usedRelays)
    {
        RelayClient[] removed;
        lock (_subscriptionsGate)
            removed = _subscribedRelays.Where(value => !usedRelays.Contains(value.Key)).Select(value => value.Value).ToArray();
        foreach (var relay in removed)
        {
            _subscriptionWorker!.Update(relay, new ChannelSubscriptionRequest { ChannelIds = [] });
            lock (_subscriptionsGate)
            {
                _subscribedRelays.Remove(relay.RelayId);
                _subscribedChannels.Remove(relay.RelayId);
                DetachRelay(relay);
            }
        }
    }

    void DetachRelay(RelayClient relay)
    {
        relay.NotificationReceived -= OnChannelNotification;
        relay.SocketConnected -= OnChannelSocketConnected;
        relay.ErrorOccurred -= OnChannelConnectionError;
    }

    void OnChannelNotification(object? sender, RpcRequest request)
    {
        if (request.Method != "channel.timeline.changed" || sender is not RelayClient relay) return;
        lock (_subscriptionsGate)
        {
            if (!_subscribedRelays.TryGetValue(relay.RelayId, out var current) || !ReferenceEquals(current, relay)) return;
            if (request.Params is null || !request.Params.TryGetValue("channel_id", out var channel) || channel.ValueKind != JsonValueKind.String
                || !request.Params.TryGetValue("head", out var head) || head.ValueKind != JsonValueKind.Number || !head.TryGetInt64(out var sequence) || sequence < 0 || sequence > 9_007_199_254_740_991)
                throw new InvalidDataException("The channel timeline notification is malformed.");
            if (_subscribedChannels[relay.RelayId].Contains(channel.GetString()!)) _refreshRequests?.Writer.TryWrite(true);
        }
    }

    void OnChannelSocketConnected(object? sender, EventArgs args) => QueueRefresh();

    void OnChannelConnectionError(object? sender, Exception exception)
    {
        if (sender is RelayClient relay) ReportBackgroundError(BackgroundOperation.Connect, relay.RelayId, exception);
    }
}
