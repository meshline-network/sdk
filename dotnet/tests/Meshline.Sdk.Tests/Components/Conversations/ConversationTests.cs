using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.Conversations;

public sealed class ConversationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static DirectMessageDraft Draft(string text = "hello") => new()
    {
        Body = new()
        {
            ContentType = "text/plain",
            Text = text
        }
    };
    [Fact]
    public async Task Conversations_sort_by_latest_message_and_persist_read_position()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        using var first = new AccountSigner();
        using var second = new AccountSigner();
        await using (var db = fixture.Database.Open())
        {
            foreach (var (sender, seconds) in new[]
            {
                (first.AccountId, 1),
                (second.AccountId, 2)
            }

            )
                db.Messages.Add(new StoredMessageRecord
                {
                    Sender = sender,
                    Recipient = fixture.Account.AccountId,
                    MessageId = Identifiers.CreateMessageId(),
                    SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
                    CreatedAt = fixture.Relay.Clock.GetUtcNow().AddSeconds(seconds),
                    IsDirect = true,
                    PayloadType = "meshline.message.direct",
                    PayloadJson = new DirectMessage { Body = Draft(sender).Body }.ToJson()
                });
            await db.SaveChangesAsync(Token);
        }

        await using (var reader = await fixture.Client.GetConversationsAsync(cancellationToken: Token))
        {
            var rows = await reader.ReadNextAsync(10, Token);

            Assert.Equal([second.AccountId, first.AccountId], rows.Select(row => row.ConversationId));
            Assert.All(rows, row => Assert.Equal(1, row.UnreadCount));
        }

        await using (var history = await fixture.Client.MessageManager.GetMessageHistoryAsync(second.AccountId, Token))
            await fixture.Client.MarkReadAsync(second.AccountId, Assert.Single(await history.ReadNextAsync(10, Token)).LocalSequence, Token);

        await fixture.ReopenAsync();

        Assert.Equal(0, (await fixture.Client.GetConversationAsync(second.AccountId, Token))!.UnreadCount);
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(first.AccountId, Token))!.UnreadCount);
    }
}
