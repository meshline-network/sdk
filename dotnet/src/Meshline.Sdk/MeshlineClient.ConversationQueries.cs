using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;

namespace Meshline;

sealed partial class MeshlineClient
{
    static IQueryable<string> QueryConversationIds(MeshlineDbContext database, string accountId) =>
        database.Messages.Where(value => value.IsDirect && (value.Sender == accountId || value.Recipient == accountId))
            .Select(value => value.Sender == accountId ? value.Recipient : value.Sender).Distinct()
            .Concat(database.Groups.Where(value => value.Membership == GroupMembershipState.Member || database.GroupEvents.Any(message => message.GroupId == value.GroupId && message.IsMessage && message.DecryptedPayloadJson != null)).Select(value => value.GroupId))
            .Concat(database.Channels.Where(value => value.IsFollowed).Select(value => value.ChannelId));

    static IQueryable<Conversation> QueryConversations(MeshlineDbContext database, string accountId, ConversationQuery? query = null, string? conversationId = null)
    {
        var reads = database.ConversationReads.AsNoTracking();
        var messages = database.Messages.AsNoTracking().Where(value => value.IsDirect && (value.Sender == accountId || value.Recipient == accountId));
        var directHeads = messages.GroupBy(value => value.Sender == accountId ? value.Recipient : value.Sender)
            .Select(group => new { ConversationId = group.Key, Sequence = group.Max(value => value.LocalSequence) });
        var direct = from head in directHeads
                     join message in messages on head.Sequence equals message.LocalSequence
                     join read in reads on head.ConversationId equals read.ConversationId into positions
                     from read in positions.DefaultIfEmpty()
                     select new
                     {
                         head.ConversationId,
                         Kind = ConversationKind.Direct,
                         Sender = (string?)message.Sender,
                         Timestamp = (DateTimeOffset?)message.CreatedAt,
                         Payload = message.PayloadJson,
                         UnreadCount = messages.LongCount(value => value.Sender == head.ConversationId && value.Sender != accountId && value.Recipient == accountId && value.LocalSequence > (read == null ? -1 : read.Sequence))
                     };

        var groupMessages = database.GroupEvents.AsNoTracking().Where(value => value.IsMessage && value.DecryptedPayloadJson != null);
        var groupHeads = groupMessages.GroupBy(value => value.GroupId).Select(group => new { GroupId = group.Key, Sequence = group.Max(value => value.Sequence) });
        var groups = from hosting in database.Groups.AsNoTracking()
                     join head in groupHeads on hosting.GroupId equals head.GroupId into heads
                     from head in heads.DefaultIfEmpty()
                     join message in groupMessages on new { hosting.GroupId, head.Sequence } equals new { message.GroupId, message.Sequence } into latest
                     from message in latest.DefaultIfEmpty()
                     join read in reads on hosting.GroupId equals read.ConversationId into positions
                     from read in positions.DefaultIfEmpty()
                     where hosting.Membership == GroupMembershipState.Member || message != null
                     select new
                     {
                         ConversationId = hosting.GroupId,
                         Kind = ConversationKind.Group,
                         Sender = message == null ? null : message.Sender,
                         Timestamp = message == null ? null : message.CreatedAt,
                         Payload = message == null ? null : message.DecryptedPayloadJson,
                         UnreadCount = groupMessages.LongCount(value => value.GroupId == hosting.GroupId && value.Sender != accountId && value.Sequence > (read == null ? -1 : read.Sequence))
                     };

        var posts = database.ChannelPosts.AsNoTracking().Where(value => !value.IsDeleted && value.PostJson != null);
        var channelHeads = posts.GroupBy(value => value.ChannelId).Select(group => new { ChannelId = group.Key, Sequence = group.Max(value => value.Sequence) });
        var channels = from channel in database.Channels.AsNoTracking()
                       join head in channelHeads on channel.ChannelId equals head.ChannelId into heads
                       from head in heads.DefaultIfEmpty()
                       join post in posts on new { channel.ChannelId, head.Sequence } equals new { post.ChannelId, post.Sequence } into latest
                       from post in latest.DefaultIfEmpty()
                       join read in reads on channel.ChannelId equals read.ConversationId into positions
                       from read in positions.DefaultIfEmpty()
                       where channel.IsFollowed
                       select new
                       {
                           ConversationId = channel.ChannelId,
                           Kind = ConversationKind.Channel,
                           Sender = post == null ? null : post.Author,
                           Timestamp = post == null ? null : post.AcceptedAt,
                           Payload = post == null ? null : post.PostJson,
                           UnreadCount = posts.LongCount(value => value.ChannelId == channel.ChannelId && value.Author != accountId && value.Sequence > (read == null ? -1 : read.Sequence))
                       };

        var records = direct.Concat(groups).Concat(channels);
        if (conversationId is not null) records = records.Where(value => value.ConversationId == conversationId);
        if (query is not null)
        {
            if (query.Kind != ConversationKind.All) records = records.Where(value => (value.Kind & query.Kind) != 0);
            if (query.UnreadOnly) records = records.Where(value => value.UnreadCount > 0);
            if (query.HasMessages is { } hasMessages) records = records.Where(value => value.Timestamp.HasValue == hasMessages);
        }
        return records.OrderByDescending(value => value.Timestamp).ThenBy(value => value.ConversationId)
            .Select(value => new { Value = value, Content = value.Payload == null ? null : ProtocolModel.FromJson<ConversationContent>(value.Payload) })
            .Select(value => new Conversation
            {
                ConversationId = value.Value.ConversationId,
                Kind = value.Value.Kind,
                UnreadCount = value.Value.UnreadCount,
                Latest = value.Value.Timestamp == null ? null : new ConversationSummary
                {
                    Sender = value.Value.Sender!,
                    Timestamp = value.Value.Timestamp.Value,
                    Text = value.Content!.Body == null ? null : value.Content.Body.Text,
                    HasAttachments = value.Content.Attachments != null && value.Content.Attachments.Value.Length > 0
                }
            });
    }
}

file sealed record ConversationContent : ProtocolModel
{
    public MessageBody? Body { get; init; }
    public ImmutableArray<ContentReference>? Attachments { get; init; }
}
