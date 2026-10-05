using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostEdit = Meshline.Models.Protocol.ChannelPostEdit;

namespace Meshline.Components;

/// <summary>
/// Manages public channel metadata, posts, follows, and local timeline synchronization.
/// </summary>
/// <param name="options">The network and account configuration for this component.</param>
/// <param name="databaseOptions">The SQLite database configuration; create its parent directory and apply migrations before initialization.</param>
/// <param name="relayClients">The shared relay pool. The application owns it and must dispose it after all dependent components.</param>
/// <param name="deviceManager">The device component sharing this network, account, database, and relay pool.</param>
/// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null. The <paramref name="options"/> argument is null.</exception>
/// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
/// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
public sealed partial class ChannelManager(ClientOptions options, DatabaseOptions databaseOptions, RelayClientPool relayClients, DeviceManager deviceManager) : ClientComponent(options)
{
    /// <summary>
    /// Occurs when a channel's locally known descriptor changes.
    /// </summary>
    public event EventHandler<ChannelInfo>? ChannelChanged;
    /// <summary>
    /// Occurs when channel post changes are committed to the local timeline.
    /// </summary>
    public event EventHandler<ChannelTimelineChangedEventArgs>? TimelineChanged;
    /// <summary>
    /// Occurs when a channel's local following state changes.
    /// </summary>
    public event EventHandler<ChannelFollowChangedEventArgs>? FollowChanged;

    readonly SemaphoreSlim _writeGate = new(1, 1);
    readonly SemaphoreSlim _stateGate = new(1, 1);

    DeviceCertificate Certificate => deviceManager.Local ?? throw new InvalidOperationException("No local device has been created.");

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">The database is bound to another network or account, or a required dependency has not been initialized.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        await EnsureDatabaseBindingAsync(database, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a signed public channel on a relay that supports channel hosting.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="name">The resource's display name.</param>
    /// <param name="description">An optional resource description.</param>
    /// <param name="moderators">Optional account identifiers permitted to moderate the channel.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The created channel's descriptor, reference, and local following state.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">Initialization or the local signing device is unavailable, the relay is inactive, or the new channel has a pending operation or conflicting descriptor revision.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    /// <exception cref="ArgumentException">The relay identifier, channel name, description, or moderator list violates protocol constraints.</exception>
    public async Task<ChannelInfo> CreateChannelAsync(string relayId, string name, string? description = null, IReadOnlyList<string>? moderators = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = await GetHostingRelayAsync(relayId, cancellationToken).ConfigureAwait(false);
            var nonce = RandomNumberGenerator.GetBytes(16);
            var now = Clock.UtcNow.ToUnixTimeSeconds();
            var request = await SignAsync(new ChannelDescriptor
            {
                ChannelId = Identifiers.DeriveChannelId(Options.AccountId, relayId, nonce, Context),
                Nonce = [.. nonce],
                Creator = Options.AccountId,
                RelayId = relayId,
                Name = name,
                Description = description,
                Moderators = moderators is null ? null : [.. moderators],
                Revision = 0,
                Status = ChannelStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                DeviceSignature = []
            }, cancellationToken).ConfigureAwait(false);
            var channel = new ChannelRef { ChannelId = request.ChannelId, RelayId = relayId };
            var pending = await SaveOperationAsync(relayId, channel.ChannelId, "channel.create", request, cancellationToken).ConfigureAwait(false);
            await PublishDescriptorAsync(relay, channel, pending, request, request, recovering: false, cancellationToken).ConfigureAwait(false);
            return await ReadChannelAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Resolves and verifies the channel descriptor and stores the resulting channel information locally.
    /// </summary>
    /// <param name="channel">The channel identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The resolved channel information and local following state.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    public async Task<ChannelInfo> GetChannelAsync(ChannelRef channel, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var relay = await GetHostingRelayAsync(channel.RelayId, cancellationToken).ConfigureAwait(false);
        await SaveDescriptorAsync(channel, await ResolveDescriptorAsync(relay, channel, null, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return await ReadChannelAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a new channel descriptor revision with the supplied field updates as the channel owner.
    /// </summary>
    /// <param name="channel">The channel identifier and hosting relay.</param>
    /// <param name="update">The field assignments and deletions to apply; unspecified fields remain unchanged.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The channel information after publishing the descriptor update.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session. The channel is closed, a previous operation is unfinished, or the accepted post or expected descriptor revision is no longer available.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    /// <exception cref="ArgumentException">The channel metadata, post content, or field update violates protocol constraints.</exception>
    /// <exception cref="UnauthorizedAccessException">The current account is not permitted to publish or modify this channel resource.</exception>
    /// <exception cref="OverflowException">Incrementing the known descriptor revision exceeds the range of a signed 64-bit integer.</exception>
    public async Task<ChannelInfo> UpdateChannelAsync(ChannelRef channel, ChannelUpdate update, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (update.Name.IsDeleted) throw new ArgumentException("A channel name cannot be deleted.", nameof(update));
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = await GetHostingRelayAsync(channel.RelayId, cancellationToken).ConfigureAwait(false);
            var current = (await ResolveDescriptorAsync(relay, channel, null, cancellationToken).ConfigureAwait(false)).Descriptor;
            EnsureCanWrite(current, ownerOnly: true);
            var request = await SignAsync(current with
            {
                Name = update.Name.IsSpecified ? update.Name.Value : current.Name,
                Description = update.Description.IsDeleted ? null : update.Description.IsSpecified ? update.Description.Value : current.Description,
                Moderators = update.Moderators.IsDeleted ? null : update.Moderators.IsSpecified ? [.. update.Moderators.Value] : current.Moderators,
                Revision = checked(current.Revision + 1),
                UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken).ConfigureAwait(false);
            var pending = await SaveOperationAsync(channel.RelayId, channel.ChannelId, "channel.update", request, cancellationToken).ConfigureAwait(false);
            await PublishDescriptorAsync(relay, channel, pending, request, request, recovering: false, cancellationToken).ConfigureAwait(false);
            return await ReadChannelAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Publishes a closed channel descriptor as the channel owner.
    /// </summary>
    /// <param name="channel">The channel identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session. The channel is closed, a previous operation is unfinished, or the accepted post or expected descriptor revision is no longer available.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    /// <exception cref="ArgumentException">The channel metadata, post content, or field update violates protocol constraints.</exception>
    /// <exception cref="UnauthorizedAccessException">The current account is not permitted to publish or modify this channel resource.</exception>
    /// <exception cref="OverflowException">Incrementing the known descriptor revision exceeds the range of a signed 64-bit integer.</exception>
    public async Task CloseChannelAsync(ChannelRef channel, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = await GetHostingRelayAsync(channel.RelayId, cancellationToken).ConfigureAwait(false);
            var current = (await ResolveDescriptorAsync(relay, channel, null, cancellationToken).ConfigureAwait(false)).Descriptor;
            EnsureCanWrite(current, ownerOnly: true);
            var final = await SignAsync(current with { Status = ChannelStatus.Closed, Revision = checked(current.Revision + 1), UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds() }, cancellationToken).ConfigureAwait(false);
            var pending = await SaveOperationAsync(channel.RelayId, channel.ChannelId, "channel.close", final, cancellationToken).ConfigureAwait(false);
            var request = new ChannelCloseRequest { ChannelId = channel.ChannelId, Revision = final.Revision, UpdatedAt = final.UpdatedAt, DeviceSignature = final.DeviceSignature };
            await PublishDescriptorAsync(relay, channel, pending, request, final, recovering: false, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Signs and publishes a public channel post, then returns its locally materialized content.
    /// </summary>
    /// <param name="channel">The channel identifier and hosting relay.</param>
    /// <param name="draft">The public post body and attachment references to publish.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The accepted post's locally materialized content and original sequence.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session. The channel is closed, a previous operation is unfinished, or the accepted post or expected descriptor revision is no longer available.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    /// <exception cref="ArgumentException">The channel metadata, post content, or field update violates protocol constraints.</exception>
    /// <exception cref="UnauthorizedAccessException">The current account is not permitted to publish or modify this channel resource.</exception>
    public async Task<ChannelPostInfo> PublishPostAsync(ChannelRef channel, ChannelPostDraft draft, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var basis = new ChannelPost { ChannelId = channel.ChannelId, MessageId = Identifiers.CreateMessageId(), Body = draft.Body, Attachments = [.. draft.Attachments], DeviceSignature = [] };
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = await GetHostingRelayAsync(channel.RelayId, cancellationToken).ConfigureAwait(false);
            EnsureCanWrite((await ResolveDescriptorAsync(relay, channel, null, cancellationToken).ConfigureAwait(false)).Descriptor);
            var request = await SignAsync(basis, cancellationToken).ConfigureAwait(false);
            var pending = await SaveOperationAsync(channel.RelayId, channel.ChannelId, "channel.post", request, cancellationToken).ConfigureAwait(false);
            var post = await PublishPostCoreAsync(relay, channel, pending, request, recovering: false, cancellationToken).ConfigureAwait(false);
            return await ReadPostAsync(post, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The accepted post has already been deleted.");
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Publishes signed field updates to an existing channel post and returns its updated local content.
    /// </summary>
    /// <param name="post">The channel and original timeline sequence identifying the post.</param>
    /// <param name="edit">The body and attachment changes to apply to the referenced post.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The post's locally materialized content after the edit.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session. The channel is closed, a previous operation is unfinished, or the accepted post or expected descriptor revision is no longer available.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    /// <exception cref="ArgumentException">The channel metadata, post content, or field update violates protocol constraints.</exception>
    /// <exception cref="UnauthorizedAccessException">The current account is not permitted to publish or modify this channel resource.</exception>
    public async Task<ChannelPostInfo> EditPostAsync(ChannelPostRef post, ChannelPostUpdate edit, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var basis = new PostEdit { ChannelId = post.Channel.ChannelId, TargetSequence = post.Sequence, Body = edit.Body, Attachments = edit.Attachments.IsDeleted ? FieldUpdate<ImmutableArray<ContentReference>>.Delete : edit.Attachments.IsSpecified ? new([.. edit.Attachments.Value]) : default, DeviceSignature = [] };
        await MutatePostAsync(post, "channel.post.edit", basis, cancellationToken).ConfigureAwait(false);
        return await ReadPostAsync(post, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The edited post is no longer available.");
    }

    /// <summary>
    /// Publishes a signed deletion for the referenced channel post.
    /// </summary>
    /// <param name="post">The channel and original timeline sequence identifying the post.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session. The channel is closed, a previous operation is unfinished, or the accepted post or expected descriptor revision is no longer available.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    /// <exception cref="ArgumentException">The channel metadata, post content, or field update violates protocol constraints.</exception>
    /// <exception cref="UnauthorizedAccessException">The current account is not permitted to publish or modify this channel resource.</exception>
    public async Task DeletePostAsync(ChannelPostRef post, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await MutatePostAsync(post, "channel.post.delete", new ChannelPostDelete { ChannelId = post.Channel.ChannelId, TargetSequence = post.Sequence, DeviceSignature = [] }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports a channel post to its hosting relay with a supplied reason.
    /// </summary>
    /// <param name="post">The channel and original timeline sequence identifying the post.</param>
    /// <param name="reason">The reason for reporting the post.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    /// <exception cref="ArgumentException">The post reference or report reason violates protocol constraints.</exception>
    public async Task ReportPostAsync(ChannelPostRef post, string reason, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var request = new ChannelReportRequest { ChannelId = post.Channel.ChannelId, TargetSequence = post.Sequence, Reason = reason };
        if (request.Validate() is { } violation) throw new ArgumentException(violation.Message, nameof(reason));
        var relay = await GetHostingRelayAsync(post.Channel.RelayId, cancellationToken).ConfigureAwait(false);
        await relay.SendHttpAsync(HttpMethod.Put, "channel.post.report", request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads and verifies a backward page of channel events and returns the available posts materialized from that page.
    /// </summary>
    /// <param name="channel">The channel identifier and hosting relay.</param>
    /// <param name="page">The requested page size and previous continuation cursor, or <see langword="null"/> for the first default-sized page.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The available posts from the event page and a cursor for earlier events when more remain.</returns>
    /// <remarks>
    /// Pass the returned <c>NextCursor</c> in the next request to continue toward older events. Deleted posts and non-post events are omitted, so a page can contain fewer posts than requested or no posts while still exposing a next cursor.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    /// <exception cref="ArgumentException">The history cursor is not a nonnegative safe integer.</exception>
    public async Task<Page<ChannelPostInfo>> LoadChannelHistoryAsync(ChannelRef channel, PageRequest? page = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        long? before = null;
        if (page?.Cursor is { } cursor)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 9_007_199_254_740_991)
                throw new ArgumentException("A channel history cursor must be a nonnegative safe integer.", nameof(page));
            before = value;
        }
        var relay = await GetHostingRelayAsync(channel.RelayId, cancellationToken).ConfigureAwait(false);
        var result = await ReadPageAsync(relay, channel, new ChannelReadQuery { ChannelId = channel.ChannelId, Before = before, Limit = page?.Limit }, false, cancellationToken).ConfigureAwait(false);
        var items = new List<ChannelPostInfo>();
        foreach (var entry in result.Events.Where(value => value.Payload is ChannelPost))
            if (await ReadPostAsync(new() { Channel = channel, Sequence = entry.Sequence }, cancellationToken).ConfigureAwait(false) is { } post) items.Add(post);
        return new(items, result.HasMore ? result.Events[0].Sequence.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>
    /// Opens a snapshot reader for locally stored, undeleted channel posts matching the supplied filters.
    /// </summary>
    /// <param name="channelId">An optional channel identifier filter; <see langword="null"/> includes all values.</param>
    /// <param name="author">An optional author account filter; <see langword="null"/> includes all authors.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A snapshot reader for the matching local results. The caller must dispose the reader after use.</returns>
    /// <remarks>
    /// This query reads local storage without fetching missing relay history. Its snapshot is fixed when opened; dispose the reader promptly and open a new reader to observe later changes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    public async Task<QueryReader<ChannelPostInfo>> GetPostsAsync(string? channelId = null, string? author = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await QueryReader<ChannelPostInfo>.OpenAsync(databaseOptions, database =>
        {
            var posts = database.ChannelPosts.AsNoTracking().Where(value => !value.IsDeleted && value.PostJson != null);
            if (channelId is not null) posts = posts.Where(value => value.ChannelId == channelId);
            if (author is not null) posts = posts.Where(value => value.Author == author);
            return from post in posts
                   join channel in database.Channels.AsNoTracking() on post.ChannelId equals channel.ChannelId
                   orderby post.ChannelId, post.Sequence
                   select PostSnapshot(post, new ChannelRef { ChannelId = post.ChannelId, RelayId = channel.RelayId });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a channel, records it as locally followed, and requests subscription refresh.
    /// </summary>
    /// <param name="channel">The channel identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// Following is local to this database. Background subscriptions and synchronization run while the component is started.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. The local device or its signing keys are unavailable, or no active relay can provide the required session.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="NotSupportedException">The hosting relay does not advertise channel hosting.</exception>
    public async Task FollowAsync(ChannelRef channel, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await GetChannelAsync(channel, cancellationToken).ConfigureAwait(false);
        await SetFollowedAsync(channel, true, cancellationToken).ConfigureAwait(false);
        QueueRefresh();
    }

    /// <summary>
    /// Clears a channel's local following state and requests subscription refresh.
    /// </summary>
    /// <param name="channel">The channel identifier and hosting relay.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// Unfollowing stops future followed-channel synchronization after refresh; it does not delete retained local posts.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">This component or a component used by the operation has been disposed.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    public async Task UnfollowAsync(ChannelRef channel, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await SetFollowedAsync(channel, false, cancellationToken).ConfigureAwait(false);
        QueueRefresh();
    }

    /// <summary>
    /// Opens a snapshot reader for locally followed channels, optionally restricted to one relay.
    /// </summary>
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
    public async Task<QueryReader<ChannelInfo>> GetFollowedAsync(string? relayId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await QueryReader<ChannelInfo>.OpenAsync(databaseOptions, database =>
        {
            var records = database.Channels.AsNoTracking().Where(value => value.IsFollowed);
            if (relayId is not null) records = records.Where(value => value.RelayId == relayId);
            return records.OrderBy(value => value.ChannelId).Select(record => ChannelSnapshot(record));
        }, cancellationToken).ConfigureAwait(false);
    }

    async Task<RelayClient> GetHostingRelayAsync(string relayId, CancellationToken cancellationToken)
    {
        var relay = await relayClients.GetAsync(relayId, deviceManager, cancellationToken).ConfigureAwait(false);
        if (!(await relay.GetDescriptorAsync(cancellationToken).ConfigureAwait(false)).Capabilities.Contains("channel.host.v1"))
            throw new NotSupportedException("The relay does not support channel hosting.");
        return relay;
    }

    void EnsureCanWrite(ChannelDescriptor descriptor, bool ownerOnly = false)
    {
        if (descriptor.Status != ChannelStatus.Active) throw new InvalidOperationException("The channel is closed.");
        if (descriptor.Creator != Options.AccountId && (ownerOnly || descriptor.Moderators?.Contains(Options.AccountId) != true))
            throw new UnauthorizedAccessException("The account is not permitted to modify this channel.");
    }

    async Task<ChannelResolveResult> ResolveDescriptorAsync(RelayClient relay, ChannelRef channel, long? revision, CancellationToken cancellationToken)
    {
        var result = await relay.SendHttpAsync<ChannelResolveResult>(HttpMethod.Get, "channel.resolve", new ChannelResolveQuery { ChannelId = channel.ChannelId, Revision = revision }, cancellationToken: cancellationToken).ConfigureAwait(false);
        ValidateDescriptor(channel, result);
        if (revision is { } expected && result.Descriptor.Revision != expected) throw new InvalidDataException("The relay returned a different descriptor revision.");
        return result;
    }

    void ValidateDescriptor(ChannelRef channel, ChannelResolveResult result)
    {
        var descriptor = result.Descriptor;
        var certificate = result.SignerCertificate;
        if (descriptor.ChannelId != channel.ChannelId || descriptor.RelayId != channel.RelayId || certificate.Account != descriptor.Creator)
            throw new InvalidDataException("The channel descriptor or signing certificate belongs to another resource or account.");
        if (result.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
        if (!Ed25519.Verify(descriptor.DeviceSignature.AsSpan(), certificate.SigningPublicKey.AsSpan(), descriptor.GetSigningInput(Context)))
            throw new CryptographicException("The channel descriptor signature is invalid.");
    }

    async Task<T> SignAsync<T>(T value, CancellationToken cancellationToken) where T : TypedProtocolModel
    {
        var input = value switch { ChannelDescriptor item => item.GetSigningInput(Context), ChannelPost item => item.GetSigningInput(Context), PostEdit item => item.GetSigningInput(Context), ChannelPostDelete item => item.GetSigningInput(Context), _ => throw new NotSupportedException() };
        ImmutableArray<byte> signature = [.. await deviceManager.SignAsync(input, cancellationToken).ConfigureAwait(false)];
        return (T)(TypedProtocolModel)(value switch { ChannelDescriptor item => item with { DeviceSignature = signature }, ChannelPost item => item with { DeviceSignature = signature }, PostEdit item => item with { DeviceSignature = signature }, ChannelPostDelete item => item with { DeviceSignature = signature }, _ => throw new NotSupportedException() });
    }

    async Task<ChannelOperationRecord> SaveOperationAsync(string relayId, string resource, string method, ProtocolModel request, CancellationToken cancellationToken)
    {
        if (request.Validate(Context) is { } violation) throw new ArgumentException(violation.Message, nameof(request));
        await using var database = new MeshlineDbContext(databaseOptions);
        if (await database.ChannelOperations.AnyAsync(value => value.RelayId == relayId && value.ResourceId == resource, cancellationToken).ConfigureAwait(false))
        {
            QueueRefresh();
            throw new InvalidOperationException("An earlier operation on this resource is unfinished. Start the component and let it recover before submitting a new operation.");
        }
        var operation = new ChannelOperationRecord { RelayId = relayId, ResourceId = resource, Method = method, DocumentJson = request.ToJson() };
        database.ChannelOperations.Add(operation);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        QueueRefresh();
        return operation;
    }

    async Task PublishDescriptorAsync(RelayClient relay, ChannelRef channel, ChannelOperationRecord operation, ProtocolModel request, ChannelDescriptor descriptor, bool recovering, CancellationToken cancellationToken)
    {
        ChannelResolveResult? accepted = null;
        try
        {
            accepted = await ResolveDescriptorAsync(relay, channel, descriptor.Revision, cancellationToken).ConfigureAwait(false);
            if (accepted.Descriptor.ToJson() != descriptor.ToJson())
            {
                await SaveDescriptorAsync(channel, accepted, CancellationToken.None, operation).ConfigureAwait(false);
                throw new InvalidOperationException("The channel revision is occupied by a different descriptor.");
            }
        }
        catch (RelayException exception) when (exception.Error.Code == "not_found") { }
        if (accepted is null)
        {
            await SendOperationAsync(relay, operation, request, recovering, cancellationToken).ConfigureAwait(false);
            accepted = new ChannelResolveResult { Descriptor = descriptor, SignerCertificate = Certificate };
        }
        await SaveDescriptorAsync(channel, accepted, CancellationToken.None, operation).ConfigureAwait(false);
        QueueRefresh();
    }

    async Task<ChannelPostRef> PublishPostCoreAsync(RelayClient relay, ChannelRef channel, ChannelOperationRecord operation, ChannelPost request, bool recovering, CancellationToken cancellationToken)
    {
        var sequence = await ReadPublicationSequenceAsync(channel, request, cancellationToken).ConfigureAwait(false);
        if (sequence is null)
        {
            var result = (await SendOperationAsync(relay, operation, request, recovering, cancellationToken).ConfigureAwait(false))!;
            sequence = result.Sequence;
        }
        var post = new ChannelPostRef { Channel = channel, Sequence = sequence.Value };
        await CompletePostOperationAsync(channel, operation, null, CancellationToken.None).ConfigureAwait(false);
        if (!recovering) await ConfirmPublicationAsync(relay, post, request, cancellationToken).ConfigureAwait(false);
        return post;
    }

    async Task MutatePostAsync<T>(ChannelPostRef post, string method, T basis, CancellationToken cancellationToken) where T : TypedProtocolModel
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = await GetHostingRelayAsync(post.Channel.RelayId, cancellationToken).ConfigureAwait(false);
            var resource = post.Channel.ChannelId + ":" + post.Sequence.ToString(CultureInfo.InvariantCulture);
            EnsureCanWrite((await ResolveDescriptorAsync(relay, post.Channel, null, cancellationToken).ConfigureAwait(false)).Descriptor);
            var request = await SignAsync(basis, cancellationToken).ConfigureAwait(false);
            var operation = await SaveOperationAsync(post.Channel.RelayId, resource, method, request, cancellationToken).ConfigureAwait(false);
            await ApplyPostOperationAsync(relay, post, operation, request, recovering: false, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    async Task ApplyPostOperationAsync(RelayClient relay, ChannelPostRef post, ChannelOperationRecord operation, TypedProtocolModel request, bool recovering, CancellationToken cancellationToken)
    {
        await SendOperationAsync(relay, operation, request, recovering, cancellationToken).ConfigureAwait(false);
        await CompletePostOperationAsync(post.Channel, operation, request is ChannelPostDelete ? post : null, CancellationToken.None).ConfigureAwait(false);
        if (!recovering) await SynchronizeAsync(relay, post.Channel, post.Sequence - 1, false, cancellationToken).ConfigureAwait(false);
    }

    async Task<SequenceResult?> SendOperationAsync(RelayClient relay, ChannelOperationRecord operation, ProtocolModel request, bool recovering, CancellationToken cancellationToken)
    {
        try
        {
            if (operation.Method == "channel.post")
            {
                var result = await relay.SendHttpAsync<SequenceResult>(HttpMethod.Put, operation.Method, request, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.Sequence is < 1 or > 9_007_199_254_740_991) throw new InvalidDataException("The relay returned an invalid channel post sequence.");
                return result;
            }
            var httpMethod = operation.Method switch
            {
                "channel.create" => HttpMethod.Post,
                "channel.update" => HttpMethod.Put,
                "channel.close" or "channel.post.delete" => HttpMethod.Delete,
                "channel.post.edit" => HttpMethod.Patch,
                _ => throw new InvalidDataException("The stored channel operation has an unexpected method.")
            };
            await relay.SendHttpAsync(httpMethod, operation.Method, request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (RelayException exception) when (!recovering && exception.Error.IsDefinitiveRejection())
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            await RemoveOperationAsync(database, operation, CancellationToken.None).ConfigureAwait(false);
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    static async Task RemoveOperationAsync(MeshlineDbContext database, ChannelOperationRecord operation, CancellationToken cancellationToken)
    {
        var pending = await database.ChannelOperations.SingleOrDefaultAsync(value => value.RelayId == operation.RelayId && value.ResourceId == operation.ResourceId && value.Method == operation.Method && value.DocumentJson == operation.DocumentJson, cancellationToken).ConfigureAwait(false);
        if (pending is not null) database.ChannelOperations.Remove(pending);
    }
}
