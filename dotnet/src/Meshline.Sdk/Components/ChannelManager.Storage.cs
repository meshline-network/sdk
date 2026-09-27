using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PostEdit = Meshline.Models.Protocol.ChannelPostEdit;

namespace Meshline.Components;

sealed partial class ChannelManager
{
    async Task<ChannelInfo> ReadChannelAsync(ChannelRef channel, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        var record = await database.Channels.AsNoTracking().SingleAsync(value => value.ChannelId == channel.ChannelId, cancellationToken).ConfigureAwait(false);
        return ChannelSnapshot(record);
    }

    async Task<ChannelPostInfo?> ReadPostAsync(ChannelPostRef post, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        var record = await database.ChannelPosts.AsNoTracking().SingleOrDefaultAsync(value => value.ChannelId == post.Channel.ChannelId && value.Sequence == post.Sequence && !value.IsDeleted, cancellationToken).ConfigureAwait(false);
        return record is null ? null : PostSnapshot(record, post.Channel);
    }

    async Task SetFollowedAsync(ChannelRef channel, bool followed, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var record = await database.Channels.FindAsync([channel.ChannelId], cancellationToken).ConfigureAwait(false);
            if (record is null || record.IsFollowed == followed) return;
            record.IsFollowed = followed;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
        FollowChanged?.Invoke(this, new(channel.ChannelId, followed));
    }

    async Task SaveDescriptorAsync(ChannelRef channel, ChannelResolveResult result, CancellationToken cancellationToken, ChannelOperationRecord? completedOperation = null)
    {
        ChannelInfo? changed = null;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var record = await GetChannelRecordAsync(database, channel, cancellationToken).ConfigureAwait(false);
            if (await StoreDescriptorAsync(database, record, result, cancellationToken).ConfigureAwait(false)) changed = ChannelSnapshot(record);
            if (completedOperation is not null) await RemoveOperationAsync(database, completedOperation, cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
        if (changed is not null) ChannelChanged?.Invoke(this, changed);
    }

    static async Task<ChannelRecord> GetChannelRecordAsync(MeshlineDbContext database, ChannelRef channel, CancellationToken cancellationToken)
    {
        var record = await database.Channels.FindAsync([channel.ChannelId], cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            record = new() { ChannelId = channel.ChannelId, RelayId = channel.RelayId };
            database.Channels.Add(record);
        }
        if (record.RelayId != channel.RelayId) throw new InvalidDataException("The channel is already bound to another relay.");
        return record;
    }

    static async Task<bool> StoreDescriptorAsync(MeshlineDbContext database, ChannelRecord record, ChannelResolveResult result, CancellationToken cancellationToken)
    {
        var descriptor = result.Descriptor;
        var json = descriptor.ToJson();
        var stored = await database.ChannelDescriptors.FindAsync([descriptor.ChannelId, descriptor.Revision], cancellationToken).ConfigureAwait(false);
        if (stored is not null && stored.DocumentJson != json) throw new InvalidDataException("The channel descriptor conflicts with an already verified revision.");
        if (stored is null) database.ChannelDescriptors.Add(new() { ChannelId = descriptor.ChannelId, Revision = descriptor.Revision, DocumentJson = json, CertificateJson = result.SignerCertificate.ToJson() });
        if (record.DescriptorJson is { } previousJson)
        {
            var previous = ProtocolModel.FromJson<ChannelDescriptor>(previousJson)!;
            if (previous.CreatedAt != descriptor.CreatedAt || previous.Creator != descriptor.Creator || !previous.Nonce.AsSpan().SequenceEqual(descriptor.Nonce.AsSpan()))
                throw new InvalidDataException("The immutable channel descriptor fields changed.");
            if (previous.Status == ChannelStatus.Closed && descriptor.Revision > previous.Revision)
                throw new InvalidDataException("A closed channel cannot have a later descriptor revision.");
        }
        if (descriptor.Revision <= record.Revision) return false;
        record.Revision = descriptor.Revision;
        record.DescriptorJson = json;
        return true;
    }

    async Task<long?> ReadPublicationSequenceAsync(ChannelRef channel, ChannelPost request, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        return await database.ChannelPosts.Where(value => value.ChannelId == channel.ChannelId && value.MessageId == request.MessageId && value.Author == Options.AccountId)
            .Select(value => (long?)value.Sequence).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task ConfirmPublicationAsync(RelayClient relay, ChannelPostRef post, ChannelPost request, CancellationToken cancellationToken)
    {
        await SynchronizeAsync(relay, post.Channel, post.Sequence - 1, false, cancellationToken).ConfigureAwait(false);
        await using var database = new MeshlineDbContext(databaseOptions);
        var stored = await database.ChannelPosts.AsNoTracking().SingleOrDefaultAsync(value => value.ChannelId == post.Channel.ChannelId && value.Sequence == post.Sequence, cancellationToken).ConfigureAwait(false);
        if (stored is null) throw new InvalidDataException("The accepted publication is absent from the channel timeline.");
        if (stored.MessageId is not null && (stored.MessageId != request.MessageId || stored.Author != Options.AccountId))
            throw new InvalidDataException("The accepted sequence identifies a different channel post.");
    }

    async Task CompletePostOperationAsync(ChannelRef channel, ChannelOperationRecord operation, ChannelPostRef? deletedPost, CancellationToken cancellationToken)
    {
        ChannelPostChange? change = null;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await GetChannelRecordAsync(database, channel, cancellationToken).ConfigureAwait(false);
            if (deletedPost is not null)
            {
                var record = await database.ChannelPosts.FindAsync([channel.ChannelId, deletedPost.Sequence], cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    record = new() { ChannelId = channel.ChannelId, Sequence = deletedPost.Sequence, AppliedThrough = deletedPost.Sequence };
                    database.ChannelPosts.Add(record);
                }
                if (!record.IsDeleted && record.PostJson is not null) change = new() { Ref = deletedPost, ChangeKind = ChannelPostChangeKind.Deleted };
                record.IsDeleted = true;
                record.PostJson = null;
            }
            await RemoveOperationAsync(database, operation, cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
        QueueRefresh();
        if (change is not null) TimelineChanged?.Invoke(this, new(channel, [change]));
    }

    async Task<ChannelReadPage> ReadPageAsync(RelayClient relay, ChannelRef channel, ChannelReadQuery query, bool advance, CancellationToken cancellationToken)
    {
        var descriptors = new Dictionary<long, ChannelResolveResult>();
        var (page, certificates) = await ReadVerifiedPageAsync(relay, channel, query, descriptors, cancellationToken).ConfigureAwait(false);
        var changes = new List<ChannelPostChange>();
        ChannelInfo? channelChange = null;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            var channelRecord = await GetChannelRecordAsync(database, channel, cancellationToken).ConfigureAwait(false);
            var operations = await database.ChannelOperations.Where(value => value.RelayId == channel.RelayId && (value.ResourceId == channel.ChannelId && value.Method == "channel.post" || value.ResourceId.StartsWith(channel.ChannelId + ":"))).ToDictionaryAsync(value => (value.ResourceId, value.Method), cancellationToken).ConfigureAwait(false);
            var pending = operations.Values.ToArray();
            var posts = new Dictionary<long, ChannelPostRecord>();
            var previous = new Dictionary<long, (string? Json, DateTimeOffset? AcceptedAt, bool IsDeleted)>();
            var catchUp = new Dictionary<long, ChannelPostRecord>();
            var boundary = query.After ?? (page.Events.IsEmpty ? -1 : page.Events[0].Sequence - 1);
            foreach (var group in page.Events.Where(value => GetTargetSequence(value) is not null).GroupBy(value => GetTargetSequence(value)!.Value))
            {
                var record = await database.ChannelPosts.FindAsync([channel.ChannelId, group.Key], cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    record = new() { ChannelId = channel.ChannelId, Sequence = group.Key, AppliedThrough = group.Key - 1 };
                    database.ChannelPosts.Add(record);
                }
                previous.Add(group.Key, (record.PostJson, record.AcceptedAt, record.IsDeleted));
                posts.Add(group.Key, record);
                if (group.LastOrDefault(value => value.Payload is ChannelPostDelete) is { } deletion)
                {
                    record.IsDeleted = true;
                    record.PostJson = null;
                    record.AppliedThrough = Math.Max(record.AppliedThrough, deletion.Sequence);
                    continue;
                }
                if (record.IsDeleted) continue;
                if (record.PostJson is not null) record.AppliedThrough = Math.Max(record.AppliedThrough, channelRecord.SyncSequence);
                if (group.Any(value => value.Payload is PostEdit && value.Sequence > record.AppliedThrough)
                    && !group.Any(value => value.Payload is ChannelPost) && record.AppliedThrough < boundary)
                    catchUp.Add(group.Key, record);
            }
            if (catchUp.Count > 0)
                await CatchUpPostsAsync(relay, channel, catchUp, boundary, descriptors, operations, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Events)
            {
                ConfirmOperation(channel, entry, certificates[entry.SignerDeviceId], operations);
                if (GetTargetSequence(entry) is { } target)
                    ApplyPostEvent(posts[target], entry, certificates[entry.SignerDeviceId]);
            }
            foreach (var record in posts.Values)
            {
                if (!record.IsDeleted && record.PostJson is null) throw new InvalidDataException("The original channel post is unavailable. The history page has not been committed.");
                record.AppliedThrough = Math.Max(record.AppliedThrough, page.Events[^1].Sequence);
                var before = previous[record.Sequence];
                if (before.IsDeleted) continue;
                var reference = new ChannelPostRef { Channel = channel, Sequence = record.Sequence };
                if (record.IsDeleted)
                {
                    if (before.Json is not null) changes.Add(new() { Ref = reference, ChangeKind = ChannelPostChangeKind.Deleted });
                }
                else if (before.Json != record.PostJson || before.AcceptedAt != record.AcceptedAt)
                    changes.Add(new() { Ref = reference, ChangeKind = before.Json is null ? ChannelPostChangeKind.Added : ChannelPostChangeKind.Edited, Info = PostSnapshot(record, channel) });
            }
            database.ChannelOperations.RemoveRange(pending.Where(value => !operations.ContainsKey((value.ResourceId, value.Method))));
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var descriptor in descriptors.Values.OrderBy(value => value.Descriptor.Revision))
                if (await StoreDescriptorAsync(database, channelRecord, descriptor, cancellationToken).ConfigureAwait(false)) channelChange = ChannelSnapshot(channelRecord);
            if (advance && !page.Events.IsEmpty) channelRecord.SyncSequence = Math.Max(channelRecord.SyncSequence, page.Events[^1].Sequence);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
        if (channelChange is not null) ChannelChanged?.Invoke(this, channelChange);
        if (changes.Count > 0) TimelineChanged?.Invoke(this, new(channel, changes));
        return page;
    }

    async Task<(ChannelReadPage Page, Dictionary<string, DeviceCertificate> Certificates)> ReadVerifiedPageAsync(RelayClient relay, ChannelRef channel, ChannelReadQuery query, Dictionary<long, ChannelResolveResult> descriptors, CancellationToken cancellationToken)
    {
        var page = await relay.SendHttpAsync<ChannelReadPage>(HttpMethod.Get, "channel.read", query, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (page.Events.IsDefault || page.Certificates.IsDefault || page.HasMore && page.Events.IsEmpty || query.Limit is { } limit && page.Events.Length > limit)
            throw new InvalidDataException("The channel history page has invalid collections or pagination.");
        var certificates = new Dictionary<string, DeviceCertificate>(StringComparer.Ordinal);
        foreach (var certificate in page.Certificates)
        {
            if (certificate.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
            if (!certificates.TryAdd(certificate.GetDeviceId(Context), certificate)) throw new InvalidDataException("The channel page contains duplicate device identities.");
        }
        var revisions = new HashSet<long>();
        var previousSequence = query.After ?? -1;
        foreach (var entry in page.Events)
        {
            if (entry.Validate() is { } violation) throw new InvalidDataException(violation.Message);
            if (entry.Sequence <= previousSequence || query.Before is { } before && entry.Sequence >= before || entry.Sequence > 9_007_199_254_740_991)
                throw new InvalidDataException("The channel page violates its requested bounds or sequence order.");
            previousSequence = entry.Sequence;
            if (!certificates.TryGetValue(entry.SignerDeviceId, out var certificate)) throw new InvalidDataException("A channel event has no signing certificate.");
            if (entry.Payload is ChannelDescriptor descriptor)
            {
                var resolved = new ChannelResolveResult { Descriptor = descriptor, SignerCertificate = certificate };
                ValidateDescriptor(channel, resolved);
                if (!revisions.Add(descriptor.Revision)) throw new InvalidDataException("A channel revision occurs more than once in the page.");
                if (descriptors.TryGetValue(descriptor.Revision, out var previous) && previous.Descriptor.ToJson() != descriptor.ToJson())
                    throw new InvalidDataException("The channel descriptor conflicts with an already verified revision.");
                descriptors[descriptor.Revision] = resolved;
            }
        }
        await using (var database = new MeshlineDbContext(databaseOptions))
        {
            foreach (var entry in page.Events.Where(value => value.Payload is ChannelPost or PostEdit or ChannelPostDelete))
            {
                if (!descriptors.ContainsKey(entry.DescriptorRev))
                {
                    var cached = await database.ChannelDescriptors.AsNoTracking().SingleOrDefaultAsync(value => value.ChannelId == channel.ChannelId && value.Revision == entry.DescriptorRev, cancellationToken).ConfigureAwait(false);
                    descriptors.Add(entry.DescriptorRev, cached is null ? await ResolveDescriptorAsync(relay, channel, entry.DescriptorRev, cancellationToken).ConfigureAwait(false)
                        : new() { Descriptor = ProtocolModel.FromJson<ChannelDescriptor>(cached.DocumentJson)!, SignerCertificate = ProtocolModel.FromJson<DeviceCertificate>(cached.CertificateJson)! });
                }
                var certificate = certificates[entry.SignerDeviceId];
                var descriptor = descriptors[entry.DescriptorRev].Descriptor;
                if (descriptor.Status != ChannelStatus.Active || descriptor.Creator != certificate.Account && descriptor.Moderators?.Contains(certificate.Account) != true)
                    throw new InvalidDataException("The content signer was not authorized by the event's descriptor revision.");
                ValidateContent(channel, entry.Payload, certificate);
            }
        }
        return (page, certificates);
    }

    void ValidateContent(ChannelRef channel, TypedProtocolModel value, DeviceCertificate certificate)
    {
        if (value.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
        var (id, input, signature) = value switch
        {
            ChannelPost post => (post.ChannelId, post.GetSigningInput(Context), post.DeviceSignature),
            PostEdit edit => (edit.ChannelId, edit.GetSigningInput(Context), edit.DeviceSignature),
            ChannelPostDelete deletion => (deletion.ChannelId, deletion.GetSigningInput(Context), deletion.DeviceSignature),
            _ => throw new NotSupportedException()
        };
        if (id != channel.ChannelId) throw new InvalidDataException("The event payload belongs to another channel.");
        if (!Ed25519.Verify(signature.AsSpan(), certificate.SigningPublicKey.AsSpan(), input)) throw new CryptographicException("The channel content signature is invalid.");
    }

    async Task CatchUpPostsAsync(RelayClient relay, ChannelRef channel, Dictionary<long, ChannelPostRecord> posts, long boundary, Dictionary<long, ChannelResolveResult> descriptors, Dictionary<(string ResourceId, string Method), ChannelOperationRecord> operations, CancellationToken cancellationToken)
    {
        var after = posts.Values.Min(value => value.AppliedThrough);
        while (after < boundary)
        {
            var (page, certificates) = await ReadVerifiedPageAsync(relay, channel, new ChannelReadQuery { ChannelId = channel.ChannelId, After = after }, descriptors, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Events)
            {
                if (GetTargetSequence(entry) is { } target && posts.TryGetValue(target, out var record)
                    && (entry.Sequence <= boundary || entry.Payload is ChannelPostDelete))
                {
                    ConfirmOperation(channel, entry, certificates[entry.SignerDeviceId], operations);
                    ApplyPostEvent(record, entry, certificates[entry.SignerDeviceId]);
                }
            }
            if (!page.HasMore || page.Events[^1].Sequence >= boundary) break;
            after = page.Events[^1].Sequence;
        }
    }

    static long? GetTargetSequence(ChannelEvent entry) => entry.Payload switch
    {
        ChannelPost => entry.Sequence,
        PostEdit edit => edit.TargetSequence,
        ChannelPostDelete deletion => deletion.TargetSequence,
        _ => null
    };

    void ConfirmOperation(ChannelRef channel, ChannelEvent entry, DeviceCertificate certificate, Dictionary<(string ResourceId, string Method), ChannelOperationRecord> operations)
    {
        var publicationKey = (channel.ChannelId, "channel.post");
        if (operations.TryGetValue(publicationKey, out var publication))
        {
            if (entry.Payload is ChannelPost post)
            {
                var request = ProtocolModel.FromJson<ChannelPost>(publication.DocumentJson)!;
                if (post.MessageId == request.MessageId && certificate.Account == Options.AccountId)
                {
                    if (publication.DocumentJson != post.ToJson())
                        throw new InvalidDataException("The synchronized post conflicts with the publication request.");
                    operations.Remove(publicationKey);
                }
            }
        }
        var method = entry.Payload switch { PostEdit => "channel.post.edit", ChannelPostDelete => "channel.post.delete", _ => null };
        if (method is null || GetTargetSequence(entry) is not { } target) return;
        var resource = channel.ChannelId + ":" + target.ToString(CultureInfo.InvariantCulture);
        if (operations.TryGetValue((resource, method), out var operation)
            && (entry.Payload is ChannelPostDelete || operation.DocumentJson == entry.Payload.ToJson()))
            operations.Remove((resource, method));
    }

    static void ApplyPostEvent(ChannelPostRecord record, ChannelEvent entry, DeviceCertificate certificate)
    {
        if (entry.Payload is ChannelPost post)
        {
            if (record.MessageId is not null) return;
            record.MessageId = post.MessageId;
            record.Author = certificate.Account;
            record.AcceptedAt = DateTimeOffset.FromUnixTimeSeconds(entry.AcceptedAt);
            if (!record.IsDeleted) record.PostJson = post.ToJson();
        }
        else if (entry.Payload is ChannelPostDelete)
        {
            record.IsDeleted = true;
            record.PostJson = null;
        }
        else if (entry.Payload is PostEdit edit && !record.IsDeleted && entry.Sequence > record.AppliedThrough)
        {
            var content = record.PostJson is null ? null : ProtocolModel.FromJson<ChannelPost>(record.PostJson);
            if (content is null) throw new InvalidDataException("The original channel post is unavailable. The history page has not been committed.");
            var extensions = content.AdditionalProperties.ToBuilder();
            foreach (var property in edit.AdditionalProperties)
            {
                if (property.Value.ValueKind == JsonValueKind.Null) extensions.Remove(property.Key);
                else extensions[property.Key] = property.Value;
            }
            content = content with
            {
                Body = edit.Body.IsDeleted ? null : edit.Body.IsSpecified ? edit.Body.Value : content.Body,
                Attachments = edit.Attachments.IsDeleted ? null : edit.Attachments.IsSpecified ? edit.Attachments.Value : content.Attachments,
                AdditionalProperties = extensions.ToImmutable()
            };
            if (content.Validate() is { } violation) throw new InvalidDataException(violation.Message);
            record.PostJson = content.ToJson();
        }
        record.AppliedThrough = Math.Max(record.AppliedThrough, entry.Sequence);
    }

    static ChannelInfo ChannelSnapshot(ChannelRecord record) => new() { Ref = new() { ChannelId = record.ChannelId, RelayId = record.RelayId }, Descriptor = record.DescriptorJson is null ? null : ProtocolModel.FromJson<ChannelDescriptor>(record.DescriptorJson), IsFollowed = record.IsFollowed };

    static ChannelPostInfo PostSnapshot(ChannelPostRecord record, ChannelRef channel)
    {
        var post = ProtocolModel.FromJson<ChannelPost>(record.PostJson!)!;
        return new() { Ref = new() { Channel = channel, Sequence = record.Sequence }, MessageId = record.MessageId!, Author = record.Author!, AcceptedAt = record.AcceptedAt, Body = post.Body, Attachments = post.Attachments is { } attachments ? attachments : null };
    }
}
