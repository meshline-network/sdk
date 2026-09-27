using Meshline.Interactions;
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
using ClientInvite = Meshline.Models.Client.GroupInvite;
using ClientUpdate = Meshline.Models.Client.GroupUpdate;

namespace Meshline.Components;

/// <summary>
/// Manages encrypted groups, membership, invitations, messages, and group key distribution.
/// </summary>
/// <param name="options">The network and account configuration for this component.</param>
/// <param name="databaseOptions">The SQLite database configuration; create its parent directory and apply migrations before initialization.</param>
/// <param name="relayClients">The shared relay pool. The application owns it and must dispose it after all dependent components.</param>
/// <param name="deviceManager">The device component sharing this network, account, database, and relay pool.</param>
/// <param name="messageManager">The message component providing the account-message stream and shared local state.</param>
/// <param name="secretProtector">The application-owned protector used to store and restore local secrets.</param>
/// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null. The <paramref name="options"/> argument is null.</exception>
/// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
/// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
public sealed partial class GroupManager(ClientOptions options, DatabaseOptions databaseOptions, RelayClientPool relayClients, DeviceManager deviceManager, MessageManager messageManager, ISecretProtector secretProtector) : ClientComponent(options)
{
    /// <summary>
    /// Occurs when locally synchronized group properties or membership information changes.
    /// </summary>
    public event EventHandler<GroupChangedEventArgs>? GroupChanged;
    /// <summary>
    /// Occurs when decrypted group messages become available in the local timeline.
    /// </summary>
    public event EventHandler<GroupTimelineChangedEventArgs>? TimelineChanged;
    /// <summary>
    /// Occurs when the relay announces changed group admission applications.
    /// </summary>
    public event EventHandler<GroupApplicationsChangedEventArgs>? ApplicationsChanged;
    /// <summary>
    /// Occurs when the relay announces changed group member key recovery requests.
    /// </summary>
    public event EventHandler<GroupKeyRecoveryChangedEventArgs>? KeyRecoveryChanged;

    readonly SemaphoreSlim _groupGate = new(1, 1);

    DeviceCertificate Certificate => deviceManager.Local ?? throw new InvalidOperationException("No local device has been created.");

    /// <summary>
    /// Creates an encrypted group and its initial member key and secret state on a hosting relay.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="options">The new group name, capacity, description, and invitation policy.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The created group's state and the local account's ownership information.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The relay identifier or requested group name, description, capacity, or invitation policy violates protocol constraints.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested member capacity exceeds the hosting relay limit.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device lacks current authorization to enqueue group-key synchronization to the account's other devices.</exception>
    public Task<GroupInfo> CreateGroupAsync(string relayId, GroupCreateOptions options, CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        GroupOperationRecord operation;
        await using (var database = new MeshlineDbContext(databaseOptions))
        {
            var relay = await GetRelayAsync(relayId, token).ConfigureAwait(false);
            var info = await relay.GetInfoAsync(token).ConfigureAwait(false);
            if (options.MemberCapacity > info.Limits.MaxGroupMembers) throw new ArgumentOutOfRangeException(nameof(options), "The capacity exceeds the hosting relay limit.");
            var nonce = RandomNumberGenerator.GetBytes(16);
            var groupId = Identifiers.DeriveGroupId(Options.AccountId, relayId, nonce, Context);
            var privateKey = RandomNumberGenerator.GetBytes(32);
            var secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                var publicKey = await SaveMemberKeyAsync(database, groupId, privateKey, token).ConfigureAwait(false);
                var commitment = Commitment(groupId, secret);
                var create = await SignAsync(new GroupCreate
                {
                    GroupId = groupId,
                    Nonce = [.. nonce],
                    Name = options.Name,
                    Description = options.Description,
                    MemberCapacity = options.MemberCapacity,
                    InvitePolicy = options.InvitePolicy,
                    Owner = new() { Account = Options.AccountId, MemberEncryptionPublicKey = [.. publicKey] },
                    ClientSecretCommitment = commitment,
                    DeviceSignature = []
                }, token).ConfigureAwait(false);
                var request = new GroupCreateRequest { Create = create, ClientSecretBox = SealClientSecret(groupId, Options.AccountId, publicKey, commitment, secret) };
                operation = NewOperation(new() { RelayId = relayId, GroupId = groupId }, "group.create", request);
                database.GroupOperations.Add(operation);
                database.Groups.Add(new() { GroupId = groupId, RelayId = relayId });
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(privateKey); CryptographicOperations.ZeroMemory(secret); }
        }
        var group = new GroupRef { RelayId = relayId, GroupId = operation.GroupId };
        await SubmitOperationAsync(operation, recovering: false, token).ConfigureAwait(false);
        await SynchronizeCoreAsync(group, token).ConfigureAwait(false);
        await ShareCurrentKeyAsync(group, token).ConfigureAwait(false);
        return await ReadGroupAsync(group, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// Resolves group state from its hosting relay and returns the local account's membership information.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The resolved group state or invitation preview with the locally known membership information.</returns>
    /// <remarks>
    /// The group-reference overload synchronizes group state. The invitation overload verifies the invitation and retrieves a preview; it does not apply for membership automatically.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    public Task<GroupInfo> GetGroupAsync(GroupRef group, CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        await SynchronizeCoreAsync(group, token).ConfigureAwait(false);
        return await ReadGroupAsync(group, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// Resolves group state from its hosting relay and returns the local account's membership information.
    /// </summary>
    /// <param name="invite">The signed invitation authorizing the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The resolved group state or invitation preview with the locally known membership information.</returns>
    /// <remarks>
    /// The group-reference overload synchronizes group state. The invitation overload verifies the invitation and retrieves a preview; it does not apply for membership automatically.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The supplied invitation differs from the relay document or the group is already bound to another relay.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    /// <exception cref="UnauthorizedAccessException">The invitation is restricted to another account.</exception>
    public Task<GroupInfo> GetGroupAsync(ClientInvite invite, CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        var verified = await ReadInviteAsync(new(invite.Group, invite.Document.InviteId), token).ConfigureAwait(false);
        if (verified.Invite.Document.ToJson() != invite.Document.ToJson()) throw new InvalidDataException("The relay invitation differs from the supplied invitation.");
        if (invite.Document.Invitee is { } target && target != Options.AccountId) throw new UnauthorizedAccessException("The invitation is intended for another account.");
        var relay = await GetRelayAsync(invite.Group.RelayId, token).ConfigureAwait(false);
        var preview = await relay.SendHttpAsync<GroupState>(HttpMethod.Get, "group.resolve", new GroupResolveQuery { GroupId = invite.Group.GroupId, InviteId = invite.Document.InviteId }, cancellationToken: token).ConfigureAwait(false);
        CheckPreview(preview, invite.Group);
        await using var database = new MeshlineDbContext(databaseOptions);
        var record = await database.Groups.FindAsync([invite.Group.GroupId], token).ConfigureAwait(false);
        if (record is null) database.Groups.Add(record = new() { GroupId = invite.Group.GroupId, RelayId = invite.Group.RelayId });
        if (record.RelayId != invite.Group.RelayId) throw new InvalidDataException("The group is bound to another relay.");
        ApplyPreview(record, preview);
        await database.SaveChangesAsync(token).ConfigureAwait(false);
        return GroupSnapshot(record);
    }, cancellationToken);

    /// <summary>
    /// Opens a snapshot reader for locally known groups matching the membership, role, and relay filters.
    /// </summary>
    /// <param name="membership">An optional local membership-state filter.</param>
    /// <param name="role">An optional group role filter; <see langword="null"/> includes all values.</param>
    /// <param name="relayId">An optional hosting relay filter; <see langword="null"/> includes all values.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The membership or role filter is not a defined enum value.</exception>
    public async Task<QueryReader<GroupInfo>> GetGroupsAsync(GroupMembershipState? membership = null, GroupRole? role = null, string? relayId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (membership is { } membershipValue && !Enum.IsDefined(membershipValue)) throw new ArgumentOutOfRangeException(nameof(membership));
        if (role is { } roleValue && !Enum.IsDefined(roleValue)) throw new ArgumentOutOfRangeException(nameof(role));
        return await QueryReader<GroupInfo>.OpenAsync(databaseOptions, database =>
        {
            var records = database.Groups.AsNoTracking().Where(value => value.Name != null);
            if (membership is not null) records = records.Where(value => value.Membership == membership);
            if (role is not null) records = records.Where(value => value.Role == role);
            if (relayId is not null) records = records.Where(value => value.RelayId == relayId);
            return records.OrderBy(value => value.GroupId).Select(record => GroupSnapshot(record));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies group metadata changes through a signed management-chain operation.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="update">The field assignments and deletions to apply; unspecified fields remain unchanged.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The updated locally synchronized group information.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">A required group field is deleted, or the resulting signed update violates protocol constraints.</exception>
    public Task<GroupInfo> UpdateGroupAsync(GroupRef group, ClientUpdate update, CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        if (update.Name.IsDeleted || update.MemberCapacity.IsDeleted || update.InvitePolicy.IsDeleted) throw new ArgumentException("The group name, member capacity, and invitation policy cannot be deleted.", nameof(update));
        await ManageAsync(group, "group.update", state =>
            new Models.Protocol.GroupUpdate { GroupId = group.GroupId, PrevHash = state.ManagementHash!, Name = update.Name, Description = update.Description, MemberCapacity = update.MemberCapacity, InvitePolicy = update.InvitePolicy, DeviceSignature = [] }, token).ConfigureAwait(false);
        return await ReadGroupAsync(group, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// Closes the group through a signed management-chain operation.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// Closure changes group lifecycle state. Previously decrypted messages can remain in local storage; callers must not assume closed-group history remains retrievable from the relay.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    public Task CloseGroupAsync(GroupRef group, CancellationToken cancellationToken = default) => RunAsync(token =>
        ManageAsync(group, "group.close", state => new GroupClose { GroupId = group.GroupId, PrevHash = state.ManagementHash!, DeviceSignature = [] }, token), cancellationToken);

    /// <summary>
    /// Encrypts and submits a group message and synchronizes its accepted timeline entry.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="draft">The plaintext message body, attachment references, and optional reply information to send.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The accepted group message as verified and materialized in local storage.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The message body, attachments, reply reference, or resulting envelope violates protocol constraints.</exception>
    public Task<GroupMessageInfo> SendMessageAsync(GroupRef group, GroupMessageDraft draft, CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        var payload = new GroupMessage { Body = draft.Body, Attachments = [.. draft.Attachments], ReplyToSeq = draft.ReplyToSequence };
        return (await SendPayloadAsync(group, payload, token).ConfigureAwait(false))!;
    }, cancellationToken);

    /// <summary>
    /// Opens a snapshot reader for locally stored decrypted group messages matching the supplied filters.
    /// </summary>
    /// <param name="groupId">An optional group identifier filter; <see langword="null"/> includes all values.</param>
    /// <param name="sender">An optional sender account filter; <see langword="null"/> includes all senders.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    public async Task<QueryReader<GroupMessageInfo>> GetMessagesAsync(string? groupId = null, string? sender = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await QueryReader<GroupMessageInfo>.OpenAsync(databaseOptions, database =>
        {
            var messages = database.GroupEvents.AsNoTracking().Where(value => value.IsMessage && value.DecryptedPayloadJson != null);
            if (groupId is not null) messages = messages.Where(value => value.GroupId == groupId);
            if (sender is not null) messages = messages.Where(value => value.Sender == sender);
            return from message in messages
                   join hosting in database.Groups.AsNoTracking() on message.GroupId equals hosting.GroupId
                   orderby message.CreatedAt, message.GroupId, message.Sequence
                   select MessageSnapshot(new GroupRef { RelayId = hosting.RelayId, GroupId = message.GroupId }, message);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an encrypted group update assigning or clearing the current account's nickname.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="nickname">The current member's group nickname, or <see langword="null"/> to clear it.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The nickname or resulting encrypted nickname update violates protocol constraints.</exception>
    public Task SetNicknameAsync(GroupRef group, string? nickname, CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        await SendPayloadAsync(group, new GroupMemberNicknameUpdate { Nickname = nickname }, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// Creates a shareable group invitation with optional use limits.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="expiresAt">The future expiration time of the invitation.</param>
    /// <param name="maxUses">An optional positive limit on uses of a shareable invitation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The signed group invitation with its hosting relay reference.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The expiration, invitee, usage limit, or resulting signed invitation violates protocol constraints.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The invitation lifetime exceeds the hosting relay limit.</exception>
    /// <exception cref="UnauthorizedAccessException">The current membership, group status, or invitation policy does not permit creating this invitation.</exception>
    public Task<ClientInvite> CreateInviteAsync(GroupRef group, DateTimeOffset expiresAt, long? maxUses = null, CancellationToken cancellationToken = default) =>
        CreateInviteCoreAsync(group, null, expiresAt, maxUses, cancellationToken);

    /// <summary>
    /// Creates a group invitation restricted to one account.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="inviteeAccountId">The account permitted to use the targeted invitation.</param>
    /// <param name="expiresAt">The future expiration time of the invitation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The signed group invitation with its hosting relay reference.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The expiration, invitee, usage limit, or resulting signed invitation violates protocol constraints.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The invitation lifetime exceeds the hosting relay limit.</exception>
    /// <exception cref="UnauthorizedAccessException">The current membership, group status, or invitation policy does not permit creating this invitation.</exception>
    public Task<ClientInvite> CreateInviteAsync(GroupRef group, string inviteeAccountId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        CreateInviteCoreAsync(group, inviteeAccountId, expiresAt, null, cancellationToken);

    /// <summary>
    /// Resolves and verifies a hosted group invitation and its use count.
    /// </summary>
    /// <param name="invite">The group, relay, and invitation identifiers to resolve or revoke.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The verified invitation with its hosting relay and observed use count.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or its signing device is uninitialized, the local device or secret protector is unavailable, or the relay cannot establish the required session.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    public Task<GroupInviteInfo> GetInviteAsync(GroupInviteRef invite, CancellationToken cancellationToken = default) => RunAsync(token => ReadInviteAsync(invite, token), cancellationToken);

    /// <summary>
    /// Reads and verifies a relay-backed page of invitations for a group.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="page">The requested page size and previous continuation cursor, or <see langword="null"/> for the first default-sized page.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A page of verified invitations and an optional relay continuation cursor.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or its signing device is uninitialized, the local device or secret protector is unavailable, or the relay cannot establish the required session.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    public Task<Page<GroupInviteInfo>> GetInvitesAsync(GroupRef group, PageRequest? page = null, CancellationToken cancellationToken = default) => RunAsync(token => ReadInvitesAsync(group, page, token), cancellationToken);

    /// <summary>
    /// Revokes a hosted group invitation through the relay.
    /// </summary>
    /// <param name="invite">The group, relay, and invitation identifiers to resolve or revoke.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    /// <exception cref="ArgumentException">The generated request contains a group, invitation, or account identifier that violates protocol constraints.</exception>
    public Task RevokeInviteAsync(GroupInviteRef invite, CancellationToken cancellationToken = default) => RunAsync(token =>
        SendUnsignedAsync(invite.Group, "group.invite.revoke", new GroupInviteQuery { GroupId = invite.Group.GroupId, InviteId = invite.InviteId }, token), cancellationToken);

    /// <summary>
    /// Submits a signed admission application using an invitation and a group-specific member encryption key.
    /// </summary>
    /// <param name="invite">The signed invitation authorizing the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// Admission remains pending until an authorized group administrator approves it. The operation retains the local member key needed to receive the group's encrypted secrets.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    public Task ApplyToGroupAsync(ClientInvite invite, CancellationToken cancellationToken = default) => RunAsync(token => ApplyAsync(invite, token), cancellationToken);

    /// <summary>
    /// Reads and verifies a relay-backed page of pending group admission applications.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="page">The requested page size and previous continuation cursor, or <see langword="null"/> for the first default-sized page.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A page of verified admission applications and an optional relay continuation cursor.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or its signing device is uninitialized, the local device or secret protector is unavailable, or the relay cannot establish the required session.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A relay-provided Unix timestamp is outside the range supported by DateTimeOffset.</exception>
    public Task<Page<GroupApplicationInfo>> GetApplicationsAsync(GroupRef group, PageRequest? page = null, CancellationToken cancellationToken = default) => RunAsync(token => ReadApplicationsAsync(group, page, token), cancellationToken);

    /// <summary>
    /// Approves pending applicants and distributes the current client secret to their member encryption keys.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accounts">The account identifiers affected by the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable. A selected request is missing, changed during paging, or expired, or the current client secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The account list is empty or contains duplicates, or the resulting signed approval violates protocol constraints.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A selected request has a relay-provided timestamp outside the supported DateTimeOffset range.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device lacks current authorization to enqueue group-key synchronization to the account's other devices.</exception>
    public Task ApproveApplicationsAsync(GroupRef group, IReadOnlyList<string> accounts, CancellationToken cancellationToken = default) => RunAsync(token => ApproveAsync(group, accounts, false, token), cancellationToken);

    /// <summary>
    /// Rejects pending admission applications for the selected accounts.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accounts">The account identifiers affected by the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    /// <exception cref="ArgumentException">The generated request contains a group, invitation, or account identifier that violates protocol constraints.</exception>
    public Task RejectApplicationsAsync(GroupRef group, IReadOnlyList<string> accounts, CancellationToken cancellationToken = default) => RunAsync(token =>
        SendUnsignedAsync(group, "group.application.reject", new GroupAccountsRequest { GroupId = group.GroupId, Accounts = [.. accounts] }, token), cancellationToken);

    /// <summary>
    /// Opens a snapshot reader for locally known group members matching the role and search filters.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="role">An optional group role filter; <see langword="null"/> includes all values.</param>
    /// <param name="search">An optional substring of the account identifier or nickname. Null or empty text disables this filter.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The role filter is not a defined enum value.</exception>
    public async Task<QueryReader<GroupMemberInfo>> GetMembersAsync(GroupRef group, GroupRole? role = null, string? search = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (role is { } roleValue && !Enum.IsDefined(roleValue)) throw new ArgumentOutOfRangeException(nameof(role));
        return await QueryReader<GroupMemberInfo>.OpenAsync(databaseOptions, database =>
        {
            var records = database.GroupMembers.AsNoTracking().Where(value => value.GroupId == group.GroupId);
            if (role is not null) records = records.Where(value => value.Role == role);
            if (!string.IsNullOrEmpty(search)) records = records.Where(value => value.AccountId.Contains(search) || value.Nickname != null && value.Nickname.Contains(search));
            return records.OrderBy(value => value.AccountId)
                .Select(value => new GroupMemberInfo { AccountId = value.AccountId, Role = value.Role, Nickname = value.Nickname, MemberEncryptionPublicKey = ImmutableArray.CreateRange(value.PublicKey) });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a snapshot reader for locally stored banned account identifiers in a group.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="search">An optional substring of the account identifier. Null or empty text disables this filter.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    public async Task<QueryReader<string>> GetBansAsync(GroupRef group, string? search = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await QueryReader<string>.OpenAsync(databaseOptions, database =>
        {
            var records = database.GroupBans.AsNoTracking().Where(value => value.GroupId == group.GroupId);
            if (!string.IsNullOrEmpty(search)) records = records.Where(value => value.AccountId.Contains(search));
            return records.OrderBy(value => value.AccountId).Select(value => value.AccountId);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Leaves a group through its signed management chain.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    public Task LeaveGroupAsync(GroupRef group, CancellationToken cancellationToken = default) => RunAsync(token =>
        ManageAsync(group, "group.member.leave", state => new GroupMemberLeave { GroupId = group.GroupId, PrevHash = state.ManagementHash!, Account = Options.AccountId, DeviceSignature = [] }, token), cancellationToken);

    /// <summary>
    /// Removes the selected accounts from group membership through its signed management chain.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accounts">The account identifiers affected by the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    public Task RemoveMembersAsync(GroupRef group, IReadOnlyList<string> accounts, CancellationToken cancellationToken = default) => RunAsync(token =>
        ManageAsync(group, "group.member.remove", state => new GroupMemberRemoval { GroupId = group.GroupId, PrevHash = state.ManagementHash!, Accounts = [.. accounts], DeviceSignature = [] }, token), cancellationToken);

    /// <summary>
    /// Bans the selected accounts through the group's signed management chain.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accounts">The account identifiers affected by the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    public Task BanAsync(GroupRef group, IReadOnlyList<string> accounts, CancellationToken cancellationToken = default) => RunAsync(token =>
        ManageAsync(group, "group.member.ban", state => new GroupMemberBan { GroupId = group.GroupId, PrevHash = state.ManagementHash!, Accounts = [.. accounts], DeviceSignature = [] }, token), cancellationToken);

    /// <summary>
    /// Clears bans for the selected accounts through the group's signed management chain.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accounts">The account identifiers affected by the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    public Task UnbanAsync(GroupRef group, IReadOnlyList<string> accounts, CancellationToken cancellationToken = default) => RunAsync(token =>
        ManageAsync(group, "group.member.unban", state => new GroupMemberUnban { GroupId = group.GroupId, PrevHash = state.ManagementHash!, Accounts = [.. accounts], DeviceSignature = [] }, token), cancellationToken);

    /// <summary>
    /// Changes a group member's role through its signed management chain.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="role">The group role to assign or select.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    public Task SetRoleAsync(GroupRef group, string accountId, GroupRole role, CancellationToken cancellationToken = default) => RunAsync(token =>
        ManageAsync(group, "group.role.update", state => new GroupRoleUpdate { GroupId = group.GroupId, PrevHash = state.ManagementHash!, Account = accountId, Role = role, DeviceSignature = [] }, token), cancellationToken);

    /// <summary>
    /// Transfers group ownership to the selected member through its signed management chain.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    public Task TransferOwnershipAsync(GroupRef group, string accountId, CancellationToken cancellationToken = default) => RunAsync(token =>
        ManageAsync(group, "group.owner.transfer", state => new GroupOwnerTransfer { GroupId = group.GroupId, PrevHash = state.ManagementHash!, NewOwnerAccount = accountId, DeviceSignature = [] }, token), cancellationToken);

    /// <summary>
    /// Generates a replacement member encryption key and requests recovery of the account's group access.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The accepted recovery request and its relay-assigned expiration time.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A relay-provided Unix timestamp is outside the range supported by DateTimeOffset.</exception>
    /// <exception cref="ArgumentException">The resulting signed recovery request violates protocol constraints.</exception>
    public Task<GroupKeyRecoveryInfo> RequestKeyRecoveryAsync(GroupRef group, CancellationToken cancellationToken = default) => RunAsync(token => RequestRecoveryAsync(group, token), cancellationToken);

    /// <summary>
    /// Reads and verifies a relay-backed page of pending group member key recovery requests.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="page">The requested page size and previous continuation cursor, or <see langword="null"/> for the first default-sized page.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A page of verified recovery requests and an optional relay continuation cursor.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or its signing device is uninitialized, the local device or secret protector is unavailable, or the relay cannot establish the required session.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A relay-provided Unix timestamp is outside the range supported by DateTimeOffset.</exception>
    public Task<Page<GroupKeyRecoveryInfo>> GetKeyRecoveryRequestsAsync(GroupRef group, PageRequest? page = null, CancellationToken cancellationToken = default) => RunAsync(token => ReadRecoveriesAsync(group, page, token), cancellationToken);

    /// <summary>
    /// Withdraws the current account's pending group member key recovery request.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    /// <exception cref="ArgumentException">The generated request contains a group, invitation, or account identifier that violates protocol constraints.</exception>
    public Task WithdrawKeyRecoveryAsync(GroupRef group, CancellationToken cancellationToken = default) => RejectKeyRecoveryAsync(group, [Options.AccountId], cancellationToken);

    /// <summary>
    /// Approves member key recovery and distributes the current client secret to the replacement keys.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accounts">The account identifiers affected by the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. The group is not writable by this account or the required group history or secret is unavailable. A selected request is missing, changed during paging, or expired, or the current client secret is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation. The management chain or requested membership, role, capacity, or actor authorization conflicts with verified group state.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The account list is empty or contains duplicates, or the resulting signed approval violates protocol constraints.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A selected request has a relay-provided timestamp outside the supported DateTimeOffset range.</exception>
    /// <exception cref="UnauthorizedAccessException">The local device lacks current authorization to enqueue group-key synchronization to the account's other devices.</exception>
    public Task ApproveKeyRecoveryAsync(GroupRef group, IReadOnlyList<string> accounts, CancellationToken cancellationToken = default) => RunAsync(token => ApproveAsync(group, accounts, true, token), cancellationToken);

    /// <summary>
    /// Rejects pending member key recovery requests for the selected accounts.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="accounts">The account identifiers affected by the operation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise group hosting.</exception>
    /// <exception cref="ArgumentException">The generated request contains a group, invitation, or account identifier that violates protocol constraints.</exception>
    public Task RejectKeyRecoveryAsync(GroupRef group, IReadOnlyList<string> accounts, CancellationToken cancellationToken = default) => RunAsync(token =>
        SendUnsignedAsync(group, "group.member.recovery.reject", new GroupAccountsRequest { GroupId = group.GroupId, Accounts = [.. accounts] }, token), cancellationToken);

    /// <summary>
    /// Prepares and commits a new group client secret, optionally rotating the owner's member encryption key.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="rotateOwnerMemberKey">Whether to replace the owner's group-specific encryption key as part of rotation.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// The operation prepares encrypted secret boxes and then commits a signed management update. Persisted preparation supports recovery from interrupted publication.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No usable local device or session is available, or an earlier operation must be confirmed before proceeding. A pending rotation must be retried with its original owner-key rotation choice.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The relay does not host groups, or the verified management history contains an unsupported operation.</exception>
    /// <exception cref="ArgumentException">The supplied group fields, account list, invitation, or message content violates the operation's protocol constraints.</exception>
    /// <exception cref="UnauthorizedAccessException">Only the current owner of an active group can rotate the client secret. The local device lacks current authorization to enqueue group-key synchronization to the account's other devices.</exception>
    public Task RotateSecretAsync(GroupRef group, bool rotateOwnerMemberKey = false, CancellationToken cancellationToken = default) => RunAsync(token => RotateAsync(group, rotateOwnerMemberKey, token), cancellationToken);

    async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await _groupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action(cancellationToken).ConfigureAwait(false); }
        finally { _groupGate.Release(); }
    }

    Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) => RunAsync(async token => { await action(token).ConfigureAwait(false); return true; }, cancellationToken);

    async Task<RelayClient> GetRelayAsync(string relayId, CancellationToken cancellationToken)
    {
        var relay = await relayClients.GetAsync(relayId, deviceManager, cancellationToken).ConfigureAwait(false);
        if (!(await relay.GetDescriptorAsync(cancellationToken).ConfigureAwait(false)).Capabilities.Contains("group.host.v1")) throw new NotSupportedException("The relay does not host groups.");
        return relay;
    }

    static GroupListQuery ListQuery(GroupRef group, PageRequest? page) => new() { GroupId = group.GroupId, Cursor = page?.Cursor, Limit = page?.Limit };

    static void ApplyPreview(GroupRecord record, GroupState preview)
    {
        if (record.ManagementHash is not null) return;
        record.Name = preview.Name;
        record.Description = preview.Description;
        record.Owner = preview.Owner;
        record.MemberCapacity = preview.MemberCapacity;
        record.MemberCount = preview.MemberCount;
        record.InvitePolicy = preview.InvitePolicy;
        record.Status = preview.Status;
    }

    static GroupInfo GroupSnapshot(GroupRecord record) => new()
    {
        Ref = new() { RelayId = record.RelayId, GroupId = record.GroupId },
        Membership = record.Membership,
        Role = record.Role,
        Group = new GroupState
        {
            GroupId = record.GroupId,
            Name = record.Name!,
            Description = record.Description,
            Owner = record.Owner!,
            MemberCapacity = record.MemberCapacity,
            MemberCount = record.MemberCount,
            InvitePolicy = record.InvitePolicy,
            Status = record.LocallyClosed ? GroupStatus.Closed : record.Status
        }
    };

    static GroupMessageInfo MessageSnapshot(GroupRef group, GroupEventRecord record)
    {
        var payload = ProtocolModel.FromJson<GroupMessage>(record.DecryptedPayloadJson!)!;
        return new()
        {
            Group = group,
            Sequence = record.Sequence,
            MessageId = record.MessageId!,
            Sender = record.Sender!,
            SenderDeviceId = record.SenderDeviceId!,
            CreatedAt = record.CreatedAt!.Value,
            Body = payload.Body,
            Attachments = payload.Attachments is { } attachments ? attachments : null,
            ReplyToSequence = payload.ReplyToSeq
        };
    }

    async Task<GroupInfo> ReadGroupAsync(GroupRef group, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        return GroupSnapshot(await database.Groups.AsNoTracking().SingleAsync(value => value.GroupId == group.GroupId, cancellationToken).ConfigureAwait(false));
    }

    static void CheckPreview(GroupState state, GroupRef group)
    {
        if (state.GroupId != group.GroupId)
            throw new InvalidDataException("The relay returned an invalid group preview.");
    }
}
