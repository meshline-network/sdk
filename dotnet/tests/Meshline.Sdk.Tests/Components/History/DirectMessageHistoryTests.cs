using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.History;

public sealed class DirectMessageHistoryTests
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Message_history_batches_follow_local_sequence_despite_older_creation_times(bool filterPeer)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await using (var database = fixture.Database.Open())
        {
            foreach (var text in new[] { "first", "second", "third" })
            {
                database.Messages.Add(new StoredMessageRecord
                {
                    Sender = peer.AccountId,
                    Recipient = fixture.Account.AccountId,
                    MessageId = Identifiers.CreateMessageId(),
                    SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
                    CreatedAt = fixture.Relay.Clock.GetUtcNow().AddSeconds(text == "first" ? 30 : text == "second" ? 20 : 10),
                    IsDirect = true,
                    PayloadType = "meshline.message.direct",
                    PayloadJson = new DirectMessage { Body = Draft(text).Body }.ToJson()
                });
                await database.SaveChangesAsync(Token);
            }
        }

        await using var reader = await fixture.Client.MessageManager.GetMessageHistoryAsync(filterPeer ? peer.AccountId : null, Token);

        Assert.Equal("first", Assert.Single(await reader.ReadNextAsync(1, Token)).Body!.Text);
        Assert.Equal("second", Assert.Single(await reader.ReadNextAsync(1, Token)).Body!.Text);
        Assert.Equal("third", Assert.Single(await reader.ReadNextAsync(1, Token)).Body!.Text);
        Assert.Empty(await reader.ReadNextAsync(1, Token));
    }

}
