using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Support;

// Seeds local projections for query/read-position tests; it does not simulate relay synchronization.
internal static class StoredMessageSetup
{
    internal static async Task AddAsync(TestClient fixture, ConversationKind kind, string id, string sender,
        long sequence, bool participating, CancellationToken cancellationToken)
    {
        await using var database = fixture.Database.Open();
        var messageId = Identifiers.CreateMessageId();
        var body = new MessageBody { ContentType = "text/plain", Text = sequence.ToString() };
        if (kind == ConversationKind.Direct)
            database.Messages.Add(new()
            {
                LocalSequence = sequence,
                Sender = sender,
                Recipient = fixture.Account.AccountId,
                MessageId = messageId,
                SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
                CreatedAt = Clock.UtcNow,
                IsDirect = true,
                PayloadType = "meshline.message.direct",
                PayloadJson = new DirectMessage { Body = body }.ToJson()
            });
        else if (kind == ConversationKind.Group)
        {
            if (!await database.Groups.AnyAsync(value => value.GroupId == id, cancellationToken))
                database.Groups.Add(new()
                {
                    GroupId = id,
                    RelayId = fixture.Relay.RelayId,
                    Membership = participating ? GroupMembershipState.Member : GroupMembershipState.Unknown
                });
            database.GroupEvents.Add(new()
            {
                GroupId = id,
                Sequence = sequence,
                PayloadJson = "{}",
                MessageId = messageId,
                Sender = sender,
                SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
                CreatedAt = Clock.UtcNow,
                IsMessage = true,
                DecryptedPayloadJson = new GroupMessage { Body = body }.ToJson()
            });
        }
        else if (kind == ConversationKind.Channel)
        {
            if (!await database.Channels.AnyAsync(value => value.ChannelId == id, cancellationToken))
                database.Channels.Add(new() { ChannelId = id, RelayId = fixture.Relay.RelayId, IsFollowed = participating });
            database.ChannelPosts.Add(new()
            {
                ChannelId = id,
                Sequence = sequence,
                MessageId = messageId,
                Author = sender,
                AcceptedAt = Clock.UtcNow,
                PostJson = new ChannelPost { ChannelId = id, MessageId = messageId, Body = body, DeviceSignature = [.. new byte[64]] }.ToJson()
            });
        }
        else throw new ArgumentOutOfRangeException(nameof(kind));
        await database.SaveChangesAsync(cancellationToken);
    }
}
