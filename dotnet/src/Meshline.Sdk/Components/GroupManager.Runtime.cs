using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Meshline.Components;

sealed partial class GroupManager
{
    readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly Lock _subscriptionGate = new();
    readonly Dictionary<string, RelayClient> _subscriptions = new(StringComparer.Ordinal);
    RelaySubscriptions? _subscriptionWorker;
    Task _refresh = Task.CompletedTask;
    Task _poll = Task.CompletedTask;

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">The database is bound to another network or account, or a required dependency has not been initialized.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="ObjectDisposedException">The required message component has been disposed.</exception>
    protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        messageManager.EnsureInitialized();
        await using var database = new MeshlineDbContext(databaseOptions);
        await EnsureDatabaseBindingAsync(database, cancellationToken).ConfigureAwait(false);
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
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        _ = Certificate;
        messageManager.TimelineChanged += OnAccountTimelineChanged;
        await messageManager.SendPayloadAsync(Options.AccountId, new AccountGroupPrivateStateRequest(), cancellationToken).ConfigureAwait(false);
        _subscriptionWorker = new("group.subscribe", new GroupSubscriptionRequest { GroupIds = [] },
            (relay, error) => ReportBackgroundError(BackgroundOperation.Connect, relay.RelayId, error), Wake, RuntimeCancellationToken);
        _refresh = RefreshGroupsAsync(RuntimeCancellationToken);
        _poll = PollGroupsAsync(RuntimeCancellationToken);
        Wake();
    }

    /// <inheritdoc/>
    protected override async Task OnStopAsync()
    {
        messageManager.TimelineChanged -= OnAccountTimelineChanged;
        await Task.WhenAll(_refresh, _poll).ConfigureAwait(false);
        RelayClient[] relays;
        lock (_subscriptionGate) { relays = _subscriptions.Values.ToArray(); _subscriptions.Clear(); }
        foreach (var relay in relays) DetachRelay(relay);
        if (_subscriptionWorker is not null) await _subscriptionWorker.DisposeAsync().ConfigureAwait(false);
        _subscriptionWorker = null;
    }

    void OnAccountTimelineChanged(object? sender, EventArgs args) => Wake();

    void Wake() => _refreshRequests.Writer.TryWrite(true);

    async Task PollGroupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Clock.Provider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) Wake();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    async Task RefreshGroupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _refreshRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_refreshRequests.Reader.TryRead(out _)) { }
                try { await ProcessAccountMessagesAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, null, exception); }
                try
                {
                    await RecoverLocalMessagesAsync(cancellationToken).ConfigureAwait(false);
                    await RunAsync(RecoverOperationsAsync, cancellationToken).ConfigureAwait(false);
                    List<GroupRecord> groups;
                    await using (var database = new MeshlineDbContext(databaseOptions))
                        groups = await database.Groups.AsNoTracking().Where(value => !value.LocallyClosed && (value.ManagementHash == null || value.Status != GroupStatus.Closed)
                            && (value.Membership == GroupMembershipState.Member || value.Membership == GroupMembershipState.Pending || value.Membership == GroupMembershipState.Unknown)).OrderBy(value => value.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
                    RemoveUnusedSubscriptions(groups.Where(value => value.Membership == GroupMembershipState.Member).Select(value => value.RelayId).ToHashSet(StringComparer.Ordinal));
                    foreach (var hosting in groups.GroupBy(value => value.RelayId))
                    {
                        var subscribed = hosting.Where(value => value.Membership == GroupMembershipState.Member).Select(value => value.GroupId).ToArray();
                        try { if (subscribed.Length > 0) await SubscribeAsync(hosting.Key, subscribed, cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Connect, hosting.Key, exception); }
                        foreach (var record in hosting)
                        {
                            try
                            {
                                await RunAsync(async token =>
                                {
                                    var group = new GroupRef { RelayId = record.RelayId, GroupId = record.GroupId };
                                    await SynchronizeCoreAsync(group, token).ConfigureAwait(false);
                                    await ShareCurrentKeyAsync(group, token).ConfigureAwait(false);
                                }, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, record.GroupId, exception); }
                        }
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, null, exception); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    async Task RecoverOperationsAsync(CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        var operations = await database.GroupOperations.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var operation in operations)
        {
            try
            {
                var relayId = await database.Groups.Where(value => value.GroupId == operation.GroupId).Select(value => value.RelayId).SingleAsync(cancellationToken).ConfigureAwait(false);
                var group = new GroupRef { RelayId = relayId, GroupId = operation.GroupId };
                if (GetTimelinePayload(ReadOperationRequest(operation)) is not null)
                {
                    try { await SynchronizeCoreAsync(group, cancellationToken, false).ConfigureAwait(false); }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        ReportBackgroundError(BackgroundOperation.Synchronize, operation.GroupId, exception);
                    }
                }
                await SubmitOperationAsync(operation, recovering: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, operation.GroupId, exception); }
        }
    }

    async Task SubscribeAsync(string relayId, string[] groups, CancellationToken cancellationToken)
    {
        var relay = await GetRelayAsync(relayId, cancellationToken).ConfigureAwait(false);
        if (!(await relay.GetDescriptorAsync(cancellationToken).ConfigureAwait(false)).Endpoints.Any(value => value.StartsWith("wss://", StringComparison.Ordinal))) return;
        var limits = (await relay.GetInfoAsync(cancellationToken).ConfigureAwait(false)).Limits;
        lock (_subscriptionGate)
        {
            if (!_subscriptions.TryGetValue(relayId, out var previous) || !ReferenceEquals(previous, relay))
            {
                if (previous is not null) DetachRelay(previous);
                _subscriptions[relayId] = relay;
                relay.NotificationReceived += OnNotification;
                relay.SocketConnected += OnSocketConnected;
                relay.ErrorOccurred += OnConnectionError;
            }
        }
        _subscriptionWorker!.Update(relay, new GroupSubscriptionRequest { GroupIds = groups.Take((int)Math.Min(limits.MaxGroupSubscriptions ?? 0, int.MaxValue)).ToImmutableArray() });
    }

    void DetachRelay(RelayClient relay)
    {
        relay.NotificationReceived -= OnNotification;
        relay.SocketConnected -= OnSocketConnected;
        relay.ErrorOccurred -= OnConnectionError;
    }

    void RemoveUnusedSubscriptions(HashSet<string> usedRelays)
    {
        RelayClient[] removed;
        lock (_subscriptionGate)
            removed = _subscriptions.Where(value => !usedRelays.Contains(value.Key)).Select(value => value.Value).ToArray();
        foreach (var relay in removed)
        {
            _subscriptionWorker!.Update(relay, new GroupSubscriptionRequest { GroupIds = [] });
            lock (_subscriptionGate)
            {
                _subscriptions.Remove(relay.RelayId);
                DetachRelay(relay);
            }
        }
    }

    void OnSocketConnected(object? sender, EventArgs args) => Wake();
    void OnConnectionError(object? sender, Exception exception) => ReportBackgroundError(BackgroundOperation.Connect, (sender as RelayClient)?.RelayId, exception);

    void OnNotification(object? sender, RpcRequest request)
    {
        if (request.Method is not ("group.timeline.changed" or "group.application.changed" or "group.member.recovery.changed")) return;
        if (sender is not RelayClient relay || request.Params is null || !request.Params.TryGetValue("group_id", out var id) || id.ValueKind != JsonValueKind.String || Identifiers.ValidateGroupId(id.GetString()!) is not null)
            throw new InvalidDataException("The group notification has an invalid group identity.");
        lock (_subscriptionGate)
            if (!_subscriptions.TryGetValue(relay.RelayId, out var current) || !ReferenceEquals(current, relay)) return;
        var group = new GroupRef { RelayId = relay.RelayId, GroupId = id.GetString()! };
        if (request.Method == "group.application.changed") ApplicationsChanged?.Invoke(this, new(group));
        else if (request.Method == "group.member.recovery.changed") KeyRecoveryChanged?.Invoke(this, new(group));
        else Wake();
    }
}
