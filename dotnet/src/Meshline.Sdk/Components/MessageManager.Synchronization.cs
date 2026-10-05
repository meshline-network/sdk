using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Components;

sealed partial class MessageManager
{
    readonly SemaphoreSlim _synchronizationGate = new(1, 1);
    readonly Lock _relayGate = new();
    readonly Dictionary<string, RelayClient> _observedRelays = new(StringComparer.Ordinal);

    readonly HashSet<string> _pendingTimelineRelays = new(StringComparer.Ordinal);
    Task _routeRecorder = Task.CompletedTask;
    string? _lastHomeRelayId;
    bool _observingRoutes;

    void OnAccountChanged(object? sender, EventArgs args)
    {
        _ = ObserveAccountRoute();
        Wake();
    }

    Task ObserveAccountRoute()
    {
        lock (_relayGate)
        {
            if (!_observingRoutes) return _routeRecorder;
            if (accountManager.Route is { } route && route.RelayId != _lastHomeRelayId)
            {
                _lastHomeRelayId = route.RelayId;
                _pendingTimelineRelays.Add(route.RelayId);
            }
            if (_pendingTimelineRelays.Count > 0)
                _routeRecorder = PersistObservedRoutesAsync(_routeRecorder);
            return _routeRecorder;
        }
    }

    async Task PersistObservedRoutesAsync(Task? previous = null)
    {
        await Task.Yield();
        if (previous is not null) await previous.ConfigureAwait(false);
        while (true)
        {
            string[] relayIds;
            lock (_relayGate) relayIds = [.. _pendingTimelineRelays];
            if (relayIds.Length == 0) return;
            try
            {
                await TransactAsync(async (database, _, token) =>
                {
                    foreach (var relayId in relayIds)
                        if (await database.AccountTimelines.FindAsync([relayId], token).ConfigureAwait(false) is null)
                            database.AccountTimelines.Add(new() { RelayId = relayId });
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportBackgroundError(BackgroundOperation.Synchronize, null, exception);
                return;
            }
            lock (_relayGate) _pendingTimelineRelays.ExceptWith(relayIds);
            _syncRequests.Writer.TryWrite(true);
        }
    }

    async Task SynchronizeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _syncRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_syncRequests.Reader.TryRead(out _)) { }
                try
                {
                    try { await accountManager.GetRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        ReportBackgroundError(BackgroundOperation.Connect, accountManager.Route?.RelayId, exception);
                    }
                    await ObserveAccountRoute().WaitAsync(cancellationToken).ConfigureAwait(false);
                    string[] relays;
                    var homeRelayId = accountManager.Route?.RelayId;
                    await using (var database = new MeshlineDbContext(databaseOptions))
                        relays = await database.AccountTimelines.OrderByDescending(value => value.RelayId == homeRelayId).Select(value => value.RelayId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var relayId in relays)
                    {
                        try
                        {
                            await SynchronizePassAsync(relayId, cancellationToken, observe: true).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, relayId, exception); }
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, null, exception); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    async Task<ResourceSyncStatus> SynchronizePassAsync(string relayId, CancellationToken cancellationToken, bool observe = false)
    {
        await _synchronizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _syncStatus.RunAsync(relayId, async () =>
            {
                var relay = await relayClients.GetAsync(relayId, deviceManager, cancellationToken).ConfigureAwait(false);
                if (observe)
                {
                    ObserveRelay(relay);
                    try
                    {
                        if ((await relay.GetDescriptorAsync(cancellationToken).ConfigureAwait(false)).Endpoints.Any(value => value.StartsWith("wss://", StringComparison.Ordinal)))
                            relay.StartNotifications();
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Connect, relayId, exception); }
                }
                await SynchronizeTimelineAsync(relay, cancellationToken).ConfigureAwait(false);
                return null;
            }, OnSyncStatusChanged, cancellationToken, preserveOnStop: !observe).ConfigureAwait(false);
        }
        finally { _synchronizationGate.Release(); }
    }

    async Task SynchronizeTimelineAsync(RelayClient relay, CancellationToken cancellationToken)
    {
        var relayId = relay.RelayId;
        while (true)
        {
            long after;
            await using (var database = new MeshlineDbContext(databaseOptions))
                after = (await database.AccountTimelines.AsNoTracking().SingleOrDefaultAsync(value => value.RelayId == relayId, cancellationToken).ConfigureAwait(false))?.Sequence ?? -1;
            var page = await relay.SendHttpAsync<MessageTimelinePage>(HttpMethod.Get, "message.timeline.sync", new MessageTimelineQuery { After = after }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page.Items.IsDefault || page.Certificates.IsDefault || page.HasMore && page.Items.IsEmpty)
                throw new InvalidDataException("The message timeline page has invalid collections or pagination.");
            var certificates = new Dictionary<string, DeviceCertificate>(StringComparer.Ordinal);
            foreach (var certificate in page.Certificates)
            {
                if (certificate.Validate(Options.Context) is { } violation) throw new InvalidDataException(violation.Message);
                if (!certificates.TryAdd(certificate.GetDeviceId(Context), certificate)) throw new InvalidDataException("The timeline page contains duplicate signing devices.");
            }
            var previous = after;
            foreach (var entry in page.Items)
            {
                if (entry.Sequence <= previous || entry.AcceptedAt < 0) throw new InvalidDataException("The timeline page has invalid sequence order or acceptance times.");
                if (!certificates.ContainsKey(entry.Envelope.FromDeviceId)) throw new InvalidDataException("A message has no signing certificate.");
                previous = entry.Sequence;
            }
            var messages = new List<MessageInfo>();
            var notifications = new List<Action>();
            try
            {
                foreach (var entry in page.Items)
                {
                    notifications.AddRange(await ReceiveMessageAsync(relayId, entry, certificates[entry.Envelope.FromDeviceId], page.HasRetentionGap == true, messages, cancellationToken).ConfigureAwait(false));
                    if (page.HasRetentionGap == true) _syncStatus.ObserveGap(relayId, OnSyncStatusChanged);
                }
                if (page.Items.IsEmpty)
                {
                    await TransactAsync(async (database, _, token) =>
                    {
                        var progress = await database.AccountTimelines.FindAsync([relayId], token).ConfigureAwait(false);
                        if (progress is null) database.AccountTimelines.Add(progress = new() { RelayId = relayId });
                        progress.HasRetentionGap |= page.HasRetentionGap == true;
                        progress.LastSynchronizedAt = Clock.UtcNow;
                    }, cancellationToken).ConfigureAwait(false);
                    if (page.HasRetentionGap == true) _syncStatus.ObserveGap(relayId, OnSyncStatusChanged);
                }
            }
            finally
            {
                PublishNotifications(notifications);
                if (messages.Count > 0) OnMessageReceived(messages);
            }
            if (!page.HasMore) return;
        }
    }

    void ObserveRelay(RelayClient relay)
    {
        lock (_relayGate)
        {
            if (_observedRelays.TryGetValue(relay.RelayId, out var current))
            {
                if (ReferenceEquals(current, relay)) return;
                DetachRelay(current);
            }
            _observedRelays[relay.RelayId] = relay;
            relay.NotificationReceived += OnNotification;
            relay.SocketConnected += OnSocketConnected;
            relay.ErrorOccurred += OnConnectionError;
        }
    }

    void DetachRelays()
    {
        lock (_relayGate)
        {
            foreach (var relay in _observedRelays.Values) DetachRelay(relay);
            _observedRelays.Clear();
        }
    }

    void DetachRelay(RelayClient relay)
    {
        relay.NotificationReceived -= OnNotification;
        relay.SocketConnected -= OnSocketConnected;
        relay.ErrorOccurred -= OnConnectionError;
    }

    void OnNotification(object? sender, RpcRequest request)
    {
        if (request.Method != "message.timeline.changed") return;
        if (request.Params is null || !request.Params.TryGetValue("head", out var head) || head.ValueKind != JsonValueKind.Number || !head.TryGetInt64(out var sequence) || sequence < 0)
            throw new InvalidDataException("The message timeline notification has an invalid head.");
        _syncRequests.Writer.TryWrite(true);
    }

    void OnSocketConnected(object? sender, EventArgs args) => _syncRequests.Writer.TryWrite(true);
    void OnConnectionError(object? sender, Exception exception) => ReportBackgroundError(BackgroundOperation.Connect, (sender as RelayClient)?.RelayId, exception);
}
