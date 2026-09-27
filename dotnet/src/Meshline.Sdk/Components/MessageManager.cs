using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Components;

/// <summary>
/// Manages contacts, encrypted direct messages, the persistent outbox, and account timeline synchronization.
/// </summary>
/// <param name="options">The network and account configuration for this component.</param>
/// <param name="databaseOptions">The SQLite database configuration; create its parent directory and apply migrations before initialization.</param>
/// <param name="relayClients">The shared relay pool. The application owns it and must dispose it after all dependent components.</param>
/// <param name="accountManager">The account component sharing this network, account, database, and relay pool.</param>
/// <param name="deviceManager">The device component sharing this network, account, database, and relay pool.</param>
/// <param name="secretProtector">The application-owned secret protector, required for operations that persist or restore protected key or message material.</param>
/// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null. The <paramref name="options"/> argument is null.</exception>
/// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
/// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
public sealed partial class MessageManager(ClientOptions options, DatabaseOptions databaseOptions, RelayClientPool relayClients, AccountManager accountManager, DeviceManager deviceManager, ISecretProtector? secretProtector = null) : ClientComponent(options)
{
    /// <summary>
    /// Occurs after account messages and their synchronization cursor are committed locally.
    /// </summary>
    public event EventHandler? TimelineChanged;
    /// <summary>
    /// Occurs after verified direct messages are committed to local storage.
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    /// <summary>
    /// Occurs when a locally tracked outgoing-message status changes.
    /// </summary>
    public event EventHandler<MessageSendStatusChangedEventArgs>? SendStatusChanged;

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">The database is bound to another network or account, or a required dependency has not been initialized.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        await EnsureDatabaseBindingAsync(database, cancellationToken).ConfigureAwait(false);
        await database.MessageOutbox.Where(value => value.State == MessageSendState.Submitting).ExecuteUpdateAsync(set => set
            .SetProperty(value => value.State, MessageSendState.SubmissionUnknown), cancellationToken).ConfigureAwait(false);
        await database.ContactRequests.Where(value => value.SendState == MessageSendState.Submitting).ExecuteUpdateAsync(set => set
            .SetProperty(value => value.SendState, MessageSendState.SubmissionUnknown), cancellationToken).ConfigureAwait(false);
        accountManager.AccountChanged += OnAccountChanged;
        lock (_relayGate) _observingRoutes = true;
        await ObserveAccountRoute().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
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
        await RefreshSendStateAsync(Options.AccountId, null, cancellationToken).ConfigureAwait(false);
        await TransactAsync(async (database, notifications, token) =>
        {
            await EnqueueAsync(database, notifications, Options.AccountId, new AccountContactSync { Records = [], RequestSnapshot = true }, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        deviceManager.DeviceChanged += OnDeviceChanged;
        deviceManager.DeviceStateChanged += OnOwnDeviceStateChanged;
        OnOwnDeviceStateChanged(this, EventArgs.Empty);
        _refresh = RefreshContactsAsync(RuntimeCancellationToken);
        _poll = PollContactsAsync(RuntimeCancellationToken);
        _sender = SendLoopAsync(RuntimeCancellationToken);
        _synchronizer = SynchronizeLoopAsync(RuntimeCancellationToken);
        _poller = PollAsync(RuntimeCancellationToken);
        _refreshRequests.Writer.TryWrite(true);
        Wake();
    }

    /// <inheritdoc/>
    protected override async Task OnStopAsync()
    {
        deviceManager.DeviceChanged -= OnDeviceChanged;
        deviceManager.DeviceStateChanged -= OnOwnDeviceStateChanged;
        await Task.WhenAll(_sender, _synchronizer, _poller, _refresh, _poll).ConfigureAwait(false);
        DetachRelays();
    }

    /// <inheritdoc/>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    protected override async ValueTask DisposeAsyncCore()
    {
        Task recorder;
        lock (_relayGate)
        {
            _observingRoutes = false;
            recorder = _routeRecorder;
        }
        accountManager.AccountChanged -= OnAccountChanged;
        await recorder.ConfigureAwait(false);
        await PersistObservedRoutesAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    /// <summary>
    /// Validates and persists an encrypted direct-message outbox operation for background delivery.
    /// </summary>
    /// <param name="recipient">The recipient account's CAIP-10 identifier.</param>
    /// <param name="draft">The plaintext message body, attachment references, and optional reply information to send.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The locally persisted outbox status; delivery proceeds in background processing.</returns>
    /// <remarks>
    /// The result describes a persisted outbox entry, not confirmed delivery. Start the component to process queued work and observe <see cref="SendStatusChanged"/> or query its status. Sending to another account requires the appropriate contact authorization.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. Required account routes, current device states, local keys, or the secret protector are unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The recipient account identifier or message payload violates protocol constraints.</exception>
    /// <exception cref="NotSupportedException">An account identifier uses an unsupported namespace.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device lacks current authorization or a direct message lacks the recipient's required contact grant.</exception>
    public async Task<MessageSendStatus> SendMessageAsync(string recipient, DirectMessageDraft draft, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var payload = new DirectMessage { Body = draft.Body, Attachments = [.. draft.Attachments], ReplyTo = draft.ReplyTo is { } reply ? new() { From = reply.Sender, MessageId = reply.MessageId } : null };
        TypedProtocolModel? authorization;
        await using (var database = new MeshlineDbContext(databaseOptions))
            authorization = await ReadAuthorizationAsync(database, recipient, cancellationToken).ConfigureAwait(false);
        await RefreshSendStateAsync(recipient, authorization, cancellationToken).ConfigureAwait(false);
        MessageOutboxRecord record = null!;
        await TransactAsync(async (database, notifications, token) =>
        {
            record = await EnqueueAsync(database, notifications, recipient, payload, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return ToStatus(record);
    }

    /// <summary>
    /// Reads a direct message from local storage by sender and message identifier.
    /// </summary>
    /// <param name="key">The sender and identifier of the direct message.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The stored direct message, or <see langword="null"/> when absent.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    public async Task<MessageInfo?> GetMessageAsync(MessageRef key, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await using var database = new MeshlineDbContext(databaseOptions);
        var record = await database.Messages.AsNoTracking().SingleOrDefaultAsync(value => value.Sender == key.Sender && value.MessageId == key.MessageId && value.IsDirect, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToMessage(record);
    }

    /// <summary>
    /// Opens a snapshot reader for locally stored direct messages, optionally restricted to one peer.
    /// </summary>
    /// <param name="peerAccountId">An optional peer account filter; <see langword="null"/> includes all direct messages.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    public async Task<QueryReader<MessageInfo>> GetMessageHistoryAsync(string? peerAccountId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await QueryReader<MessageInfo>.OpenAsync(databaseOptions, database =>
        {
            var records = database.Messages.AsNoTracking().Where(value => value.IsDirect);
            if (peerAccountId is not null)
                records = records.Where(value => value.Sender == Options.AccountId && value.Recipient == peerAccountId || value.Sender == peerAccountId && value.Recipient == Options.AccountId);
            return records.OrderBy(value => value.CreatedAt).ThenBy(value => value.MessageId).ThenBy(value => value.Sender)
                .Select(record => ToMessage(record));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a snapshot reader for direct-message outbox entries matching the recipient and state filters.
    /// </summary>
    /// <param name="recipient">An optional recipient account filter; <see langword="null"/> includes all recipients.</param>
    /// <param name="state">The outbox states to include; flags may be combined and the default includes all states.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="ArgumentException">The send-state filter contains unsupported flags.</exception>
    public async Task<QueryReader<MessageSendStatus>> GetOutboxAsync(string? recipient = null, MessageSendState state = MessageSendState.All, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if ((state & ~MessageSendState.All) != 0) throw new ArgumentException("The query contains an unsupported send state.", nameof(state));
        return await QueryReader<MessageSendStatus>.OpenAsync(databaseOptions, database =>
        {
            var records = database.MessageOutbox.AsNoTracking().Where(value => value.IsDirect);
            if (recipient is not null) records = records.Where(value => value.Recipient == recipient);
            if (state != MessageSendState.All) records = records.Where(value => (value.State & state) != 0);
            return records.OrderBy(value => value.CreatedAt).ThenBy(value => value.MessageId)
                .Select(record => new MessageSendStatus { MessageId = record.MessageId, Recipient = record.Recipient, CreatedAt = record.CreatedAt, State = record.State, AcceptedRelayId = record.AcceptedAt == null ? null : record.RelayId, AcceptedAt = record.AcceptedAt, ErrorMessage = record.ErrorMessage });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the locally tracked outbox status of a message.
    /// </summary>
    /// <param name="messageId">The canonical message identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The outbox status, or <see langword="null"/> when the message has no local outbox entry.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    public async Task<MessageSendStatus?> GetSendStatusAsync(string messageId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await using var database = new MeshlineDbContext(databaseOptions);
        var record = await database.MessageOutbox.AsNoTracking().SingleOrDefaultAsync(value => value.MessageId == messageId, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToStatus(record);
    }

    /// <summary>
    /// Cancels an outgoing message only while its local outbox state is queued.
    /// </summary>
    /// <param name="messageId">The canonical message identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns><see langword="true"/> if this call changes a queued message to canceled; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// Missing messages and messages in any state other than queued are left unchanged. This does not retract a submitted or accepted message.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    public async Task<bool> CancelMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await CancelAsync(messageId, cancellationToken).ConfigureAwait(false);
    }

    void OnTimelineChanged() => TimelineChanged?.Invoke(this, EventArgs.Empty);

    void OnMessageReceived(IReadOnlyList<MessageInfo> messages) => MessageReceived?.Invoke(this, new(messages));

    void OnSendChanged(MessageOutboxRecord record)
    {
        if (record.IsDirect) SendStatusChanged?.Invoke(this, new(ToStatus(record)));
    }
}
