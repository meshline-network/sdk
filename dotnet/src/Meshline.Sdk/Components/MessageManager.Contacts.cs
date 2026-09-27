using Meshline.Identity;
using Meshline.Models;
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

namespace Meshline.Components;

sealed partial class MessageManager
{
    /// <summary>
    /// Occurs when a local incoming or outgoing contact request changes.
    /// </summary>
    public event EventHandler<ContactRequestChangedEventArgs>? ContactRequestChanged;
    /// <summary>
    /// Occurs when a local contact's relationship, alias, or authorization changes.
    /// </summary>
    public event EventHandler<ContactChangedEventArgs>? ContactChanged;

    DeviceCertificate Certificate => deviceManager.Local ?? throw new InvalidOperationException("No local device has been created.");

    /// <summary>
    /// Creates an expiring, device-signed invitation to contact the current account.
    /// </summary>
    /// <param name="expiresAt">The future expiration time of the invitation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The signed contact invitation.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No local device has been created, or its signing key cannot be loaded without a secret protector.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="InvalidDataException">The stored local device certificate is JSON null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The invitation does not expire in the future.</exception>
    public async Task<ContactInvite> CreateInviteAsync(DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var expiry = expiresAt.ToUnixTimeSeconds();
        if (expiry <= Clock.UtcNow.ToUnixTimeSeconds()) throw new ArgumentOutOfRangeException(nameof(expiresAt), "The invitation must expire in the future.");
        var invite = new ContactInvite { Inviter = Options.AccountId, SignerDeviceId = Certificate.GetDeviceId(Context), ExpiresAt = expiry, DeviceSignature = [] };
        return invite with { DeviceSignature = [.. await deviceManager.SignAsync(invite.GetSigningInput(Context), cancellationToken).ConfigureAwait(false)] };
    }

    /// <summary>
    /// Creates an outgoing contact request and queues signed consent for delivery to the account.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="note">An optional note accompanying the contact request.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The locally stored outgoing contact request and its associated outbox information.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. Required account routes, current device states, local keys, or the secret protector are unavailable. The account is already a contact or the prospective contact has no currently authorized devices.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The target account is invalid or is the current account, or the invitation or note violates protocol constraints.</exception>
    /// <exception cref="NotSupportedException">An account identifier uses an unsupported namespace.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device lacks current authorization or a direct message lacks the recipient's required contact grant.</exception>
    public Task<ContactRequestInfo> AddContactAsync(string accountId, string? note = null, CancellationToken cancellationToken = default) => AddContactCoreAsync(accountId, null, note, cancellationToken);

    /// <summary>
    /// Creates an outgoing contact request and queues signed consent for delivery to the account.
    /// </summary>
    /// <param name="invite">The signed invitation authorizing the operation.</param>
    /// <param name="note">An optional note accompanying the contact request.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The locally stored outgoing contact request and its associated outbox information.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. Required account routes, current device states, local keys, or the secret protector are unavailable. The account is already a contact or the prospective contact has no currently authorized devices.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The target account is invalid or is the current account, or the invitation or note violates protocol constraints.</exception>
    /// <exception cref="NotSupportedException">An account identifier uses an unsupported namespace.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device lacks current authorization or a direct message lacks the recipient's required contact grant.</exception>
    public Task<ContactRequestInfo> AddContactAsync(ContactInvite invite, string? note = null, CancellationToken cancellationToken = default) => AddContactCoreAsync(invite.Inviter, invite, note, cancellationToken);

    async Task<ContactRequestInfo> AddContactCoreAsync(string accountId, ContactInvite? invite, string? note, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        CheckContactAccount(accountId);
        var recipientState = invite is null ? await deviceManager.GetDeviceStateAsync(accountId, cancellationToken).ConfigureAwait(false)
            : await deviceManager.GetDeviceStateAsync(invite, cancellationToken).ConfigureAwait(false);
        if (recipientState is null || CurrentCertificates(recipientState).Length == 0) throw new InvalidOperationException("The contact has no currently authorized devices.");
        var consent = await CreateConsentAsync(accountId, note, cancellationToken).ConfigureAwait(false);
        ContactRequestInfo result = null!;
        await TransactAsync(async (database, notifications, token) =>
        {
            if (await database.Contacts.FindAsync([accountId], token).ConfigureAwait(false) is { State: ContactRelationshipState.Active })
                throw new InvalidOperationException("The account is already a contact.");
            if (await database.ContactRequests.FindAsync([accountId, ContactRequestDirection.Outgoing], token).ConfigureAwait(false) is { } previous)
            {
                if (previous.SendState is not (MessageSendState.Canceled or MessageSendState.Failed))
                {
                    result = RequestSnapshot(previous);
                    return;
                }
                database.ContactRequests.Remove(previous);
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            }
            var outgoing = await EnqueueAsync(database, notifications, accountId, consent, token, invite).ConfigureAwait(false);
            var record = new ContactRequestRecord { AccountId = accountId, Direction = ContactRequestDirection.Outgoing, Note = note, CreatedAt = outgoing.CreatedAt, MessageId = outgoing.MessageId, ConsentJson = consent.ToJson(), SendState = outgoing.State };
            database.ContactRequests.Add(record);
            result = RequestSnapshot(record, outgoing.State);
            notifications.Add(() => ContactRequestChanged?.Invoke(this, new(accountId, record.Direction, result, ContactRequestChangeKind.Added)));
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Opens a snapshot reader for locally stored contact requests matching the supplied filters.
    /// </summary>
    /// <param name="accountId">An optional account identifier filter; <see langword="null"/> includes all values.</param>
    /// <param name="direction">The incoming or outgoing request directions to include; defaults to both.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="ArgumentException">The direction filter contains unsupported flags.</exception>
    public async Task<QueryReader<ContactRequestInfo>> GetContactRequestsAsync(string? accountId = null, ContactRequestDirection direction = ContactRequestDirection.All, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if ((direction & ~ContactRequestDirection.All) != 0) throw new ArgumentException("The query contains an unsupported contact request direction.", nameof(direction));
        return await QueryReader<ContactRequestInfo>.OpenAsync(databaseOptions, database =>
        {
            var records = database.ContactRequests.AsNoTracking();
            if (accountId is not null) records = records.Where(value => value.AccountId == accountId);
            if (direction != ContactRequestDirection.All) records = records.Where(value => (value.Direction & direction) != 0);
            return records.OrderBy(value => value.CreatedAt).ThenBy(value => value.AccountId).ThenBy(value => value.Direction)
                .Select(value => new ContactRequestInfo { AccountId = value.AccountId, Direction = value.Direction, CreatedAt = value.CreatedAt, Note = value.Note, MessageId = value.MessageId, SendState = value.SendState });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies an incoming request, activates the contact, and queues reciprocal consent and account-device synchronization.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The newly active contact with its updated grant states.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. Required account routes, current device states, local keys, or the secret protector are unavailable. There is no incoming request, its device state is unavailable, or the request changed while being accepted.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The stored contact grant or resulting consent or synchronization message violates protocol constraints.</exception>
    /// <exception cref="NotSupportedException">An account identifier uses an unsupported namespace.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device lacks current authorization or a direct message lacks the recipient's required contact grant.</exception>
    public async Task<ContactInfo> AcceptContactRequestAsync(string accountId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        CheckContactAccount(accountId);
        ContactRequestRecord incoming;
        await using (var database = new MeshlineDbContext(databaseOptions))
            incoming = await database.ContactRequests.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == accountId && value.Direction == ContactRequestDirection.Incoming, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("There is no incoming contact request from this account.");
        var remote = ProtocolModel.FromJson<ContactConsent>(incoming.ConsentJson)!;
        var state = await deviceManager.GetDeviceStateAsync(remote.Grant, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The contact device state is unavailable.");
        var grant = ValidateGrant(remote.Grant, state, Context);
        var consent = await CreateConsentAsync(accountId, null, cancellationToken).ConfigureAwait(false);
        await TransactAsync(async (database, notifications, token) =>
        {
            var selected = await database.ContactRequests.FindAsync([accountId, ContactRequestDirection.Incoming], token).ConfigureAwait(false);
            if (selected?.ConsentJson != incoming.ConsentJson) throw new InvalidOperationException("The incoming contact request changed while it was being accepted.");
            await EnqueueAsync(database, notifications, accountId, consent, token, grant).ConfigureAwait(false);
            var record = await GetOrCreateContactAsync(database, accountId, token).ConfigureAwait(false);
            record.State = ContactRelationshipState.Active;
            record.GrantFromJson = grant.ToJson();
            record.GrantToJson = consent.Grant.ToJson();
            record.UpdatedAt = NextContactTime(record.UpdatedAt);
            await ClearRequestsAsync(database, accountId, notifications, token).ConfigureAwait(false);
            await QueueSyncAsync(database, notifications, record, token).ConfigureAwait(false);
            NotifyContact(notifications, record, ContactChangeKind.Relationship | ContactChangeKind.Authorization);
        }, cancellationToken).ConfigureAwait(false);
        return (await GetContactAsync(accountId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Removes an incoming contact request from local storage if it exists.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    public async Task DismissContactRequestAsync(string accountId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await TransactAsync(async (database, notifications, token) =>
        {
            var record = await database.ContactRequests.FindAsync([accountId, ContactRequestDirection.Incoming], token).ConfigureAwait(false);
            if (record is null) return;
            database.ContactRequests.Remove(record);
            notifications.Add(() => ContactRequestChanged?.Invoke(this, new(accountId, record.Direction, null, ContactRequestChangeKind.Removed)));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a snapshot reader for active local contacts, optionally matching an account identifier or alias.
    /// </summary>
    /// <param name="search">An optional substring of the account identifier or alias. Null or empty text disables this filter.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    public async Task<QueryReader<ContactInfo>> GetContactsAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await QueryReader<ContactInfo>.OpenAsync(databaseOptions, database =>
        {
            var records = database.Contacts.AsNoTracking().Where(value => value.State == ContactRelationshipState.Active);
            if (!string.IsNullOrEmpty(search)) records = records.Where(value => value.AccountId.Contains(search) || value.Alias != null && value.Alias.Contains(search));
            return QueryContacts(database, records);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an active contact from local storage by account identifier.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The active contact, or <see langword="null"/> when no active contact exists for the account.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    public async Task<ContactInfo?> GetContactAsync(string accountId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await using var database = new MeshlineDbContext(databaseOptions);
        return await QueryContacts(database, database.Contacts.AsNoTracking().Where(value => value.State == ContactRelationshipState.Active && value.AccountId == accountId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    IQueryable<ContactInfo> QueryContacts(MeshlineDbContext database, IQueryable<ContactStateRecord> records)
    {
        var context = Context;
        var states = from contact in records
                     join remote in database.DeviceStates.AsNoTracking() on contact.AccountId equals remote.AccountId into remoteStates
                     from remote in remoteStates.DefaultIfEmpty()
                     join own in database.DeviceStates.AsNoTracking() on Options.AccountId equals own.AccountId into ownStates
                     from own in ownStates.DefaultIfEmpty()
                     orderby contact.AccountId
                     select new { Contact = contact, RemoteJson = remote == null ? null : remote.DocumentJson, OwnJson = own == null ? null : own.DocumentJson };
        return states.Select(value => ReadContactSnapshot(value.Contact, value.RemoteJson, value.OwnJson, context));
    }

    /// <summary>
    /// Updates an active contact's private alias and queues synchronization to the account's other devices.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="alias">The private contact alias to store, or <see langword="null"/> to clear it.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The contact snapshot containing the updated alias.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The account is not an active contact or a required account-device synchronization message cannot be enqueued.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="ArgumentException">A nonempty alias contains only whitespace or exceeds 256 UTF-8 bytes.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device is not authorized to enqueue contact synchronization.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="InvalidDataException">The device certificates used for the account synchronization message contain duplicate devices or another account.</exception>
    public async Task<ContactInfo> SetAliasAsync(string accountId, string? alias, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (alias is { Length: > 0 } && (string.IsNullOrWhiteSpace(alias) || Encoding.UTF8.GetByteCount(alias) > 256)) throw new ArgumentException("The alias must contain non-whitespace text and fit in 256 UTF-8 bytes.", nameof(alias));
        await TransactAsync(async (database, notifications, token) =>
        {
            var record = await database.Contacts.FindAsync([accountId], token).ConfigureAwait(false);
            if (record is not { State: ContactRelationshipState.Active }) throw new InvalidOperationException("The account is not an active contact.");
            if (record.Alias == alias) return;
            record.Alias = alias;
            record.UpdatedAt = NextContactTime(record.UpdatedAt);
            await QueueSyncAsync(database, notifications, record, token).ConfigureAwait(false);
            NotifyContact(notifications, record, ContactChangeKind.Alias);
        }, cancellationToken).ConfigureAwait(false);
        return (await GetContactAsync(accountId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Marks a contact deleted, clears local grants and requests, and queues account-device synchronization.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// This updates local contact state and synchronizes the account's other devices. It does not erase existing message history or retract already delivered grants or messages.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The home route, local device, or device state needed for contact synchronization is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device is not authorized to enqueue contact synchronization.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The account identifier is invalid or identifies the current account.</exception>
    /// <exception cref="NotSupportedException">The account identifier uses an unsupported namespace.</exception>
    /// <exception cref="InvalidDataException">The device certificates used for the account synchronization message contain duplicate devices or another account.</exception>
    public async Task RemoveContactAsync(string accountId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        CheckContactAccount(accountId);
        await TransactAsync(async (database, notifications, token) =>
        {
            var record = await GetOrCreateContactAsync(database, accountId, token).ConfigureAwait(false);
            record.State = ContactRelationshipState.Deleted;
            record.GrantFromJson = null;
            record.GrantToJson = null;
            record.ConfirmedGrantToJson = null;
            record.RequiredDeviceRevision = null;
            record.UpdatedAt = NextContactTime(record.UpdatedAt);
            await ClearRequestsAsync(database, accountId, notifications, token).ConfigureAwait(false);
            await QueueSyncAsync(database, notifications, record, token).ConfigureAwait(false);
            NotifyContact(notifications, record, ContactChangeKind.Deleted);
        }, cancellationToken).ConfigureAwait(false);
    }

    async Task<ContactConsent> CreateConsentAsync(string accountId, string? note, CancellationToken cancellationToken)
    {
        var state = await deviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The account device state is unavailable.");
        if (state.ValidateDeviceAuthorization(Certificate.GetDeviceId(Context), Context) is { } violation) throw new UnauthorizedAccessException(violation.Message);
        var grant = new ContactGrant { Grantor = Options.AccountId, Grantee = accountId, Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty };
        grant = await AddSignatureAsync(grant, cancellationToken).ConfigureAwait(false);
        var consent = new ContactConsent { DeviceState = state, Grant = grant, Note = note };
        if (consent.Validate(Context) is { } consentViolation) throw new ArgumentException(consentViolation.Message, nameof(note));
        return consent;
    }

    async Task<ContactGrant> AddSignatureAsync(ContactGrant grant, CancellationToken cancellationToken)
    {
        var id = Certificate.GetDeviceId(Context);
        if (grant.Signatures.ContainsKey(id)) return grant;
        var signature = await deviceManager.SignAsync(grant.GetSigningInput(Context), cancellationToken).ConfigureAwait(false);
        return grant with { Signatures = grant.Signatures.Add(id, [.. signature]) };
    }

    void CheckContactAccount(string accountId)
    {
        if (accountId == Options.AccountId) throw new ArgumentException("An account cannot add itself as a contact.", nameof(accountId));
        if (AccountAdapter.ValidateAccountId(accountId) is { } violation) throw new ArgumentException(violation.Message, nameof(accountId));
    }

    static DateTimeOffset NextContactTime(DateTimeOffset previous) => DateTimeOffset.FromUnixTimeSeconds(Math.Max(Clock.UtcNow.ToUnixTimeSeconds(), checked(previous.ToUnixTimeSeconds() + 1)));

    static async Task<ContactStateRecord> GetOrCreateContactAsync(MeshlineDbContext database, string accountId, CancellationToken cancellationToken)
    {
        var record = await database.Contacts.FindAsync([accountId], cancellationToken).ConfigureAwait(false);
        if (record is null) database.Contacts.Add(record = new() { AccountId = accountId, UpdatedAt = DateTimeOffset.UnixEpoch });
        return record;
    }

    async Task ClearRequestsAsync(MeshlineDbContext database, string accountId, List<Action> notifications, CancellationToken cancellationToken)
    {
        var records = await database.ContactRequests.Where(value => value.AccountId == accountId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            database.ContactRequests.Remove(record);
            notifications.Add(() => ContactRequestChanged?.Invoke(this, new(accountId, record.Direction, null, ContactRequestChangeKind.Removed)));
        }
    }

    Task<MessageOutboxRecord> QueueSyncAsync(MeshlineDbContext database, List<Action> notifications, ContactStateRecord record, CancellationToken cancellationToken) =>
        EnqueueAsync(database, notifications, Options.AccountId, new AccountContactSync { Records = [ToProtocolRecord(record)] }, cancellationToken);

    static ContactRecord ToProtocolRecord(ContactStateRecord record) => new()
    {
        Account = record.AccountId,
        Alias = record.Alias,
        Status = record.State,
        UpdatedAt = record.UpdatedAt.ToUnixTimeSeconds(),
        GrantFromContact = ReadUnexpiredGrant(record.GrantFromJson),
        GrantToContact = ReadUnexpiredGrant(record.GrantToJson)
    };

    static ContactGrant? ReadUnexpiredGrant(string? json)
    {
        var grant = json is null ? null : ProtocolModel.FromJson<ContactGrant>(json);
        return grant?.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds() ? null : grant;
    }

    void NotifyContact(List<Action> notifications, ContactStateRecord record, ContactChangeKind kind)
    {
        var snapshot = record.State == ContactRelationshipState.Deleted ? null : ContactSnapshot(record, null, deviceManager.DeviceState, Context);
        notifications.Add(() => ContactChanged?.Invoke(this, new(record.AccountId, snapshot, kind)));
    }

    static ContactRequestInfo RequestSnapshot(ContactRequestRecord record, MessageSendState? state = null) => new() { AccountId = record.AccountId, Direction = record.Direction, CreatedAt = record.CreatedAt, Note = record.Note, MessageId = record.MessageId, SendState = state ?? record.SendState };

    static ContactInfo ReadContactSnapshot(ContactStateRecord record, string? remoteJson, string? ownJson, NetworkContext context) =>
        ContactSnapshot(record, remoteJson is null ? null : ProtocolModel.FromJson<AccountDeviceState>(remoteJson), ownJson is null ? null : ProtocolModel.FromJson<AccountDeviceState>(ownJson), context);

    static ContactInfo ContactSnapshot(ContactStateRecord record, AccountDeviceState? remote, AccountDeviceState? own, NetworkContext context) => new()
    {
        AccountId = record.AccountId,
        Alias = record.Alias,
        State = record.State,
        UpdatedAt = record.UpdatedAt,
        GrantFromContact = GrantState(record.GrantFromJson, remote, context),
        GrantToContact = GrantState(record.GrantToJson, own, context)
    };

    static ContactGrantState GrantState(string? json, AccountDeviceState? state, NetworkContext context)
    {
        if (json is null) return ContactGrantState.Missing;
        var grant = ProtocolModel.FromJson<ContactGrant>(json)!;
        if (grant.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()) return ContactGrantState.Expired;
        if (state is null) return ContactGrantState.Unknown;
        return FilterGrantSignatures(grant, state, context).Signatures.IsEmpty ? ContactGrantState.InsufficientSignatures : ContactGrantState.Valid;
    }
}
