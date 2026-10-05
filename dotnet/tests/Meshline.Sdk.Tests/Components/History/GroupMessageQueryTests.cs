using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.History;

public sealed class GroupMessageQueryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Message_batches_follow_group_then_sequence_despite_older_creation_times(bool filterGroup)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        const string first = "grp_AAAAAAAAAAAAAAAAAAAAAA";
        const string second = "grp_AAAAAAAAAAAAAAAAAAAAAQ";
        await using (var database = fixture.Database.Open())
        {
            database.Groups.Add(new() { GroupId = first, RelayId = fixture.Relay.RelayId });
            database.Groups.Add(new() { GroupId = second, RelayId = fixture.Relay.RelayId });
            foreach (var (groupId, sequence, seconds) in new[] { (second, 2L, 30), (first, 9L, 10), (second, 7L, 20), (first, 3L, 40) })
            {
                database.GroupEvents.Add(new()
                {
                    GroupId = groupId,
                    Sequence = sequence,
                    PayloadJson = "{}",
                    MessageId = Identifiers.CreateMessageId(),
                    Sender = fixture.Account.AccountId,
                    SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
                    CreatedAt = fixture.Relay.Clock.GetUtcNow().AddSeconds(seconds),
                    IsMessage = true,
                    DecryptedPayloadJson = new GroupMessage { Body = new() { ContentType = "text/plain", Text = $"{groupId}/{sequence}" } }.ToJson()
                });
            }
            await database.SaveChangesAsync(Token);
        }

        await using var reader = await fixture.Client.GroupManager.GetMessagesAsync(filterGroup ? first : null, cancellationToken: Token);
        var expected = filterGroup ? new[] { (first, 3L), (first, 9L) } : [(first, 3L), (first, 9L), (second, 2L), (second, 7L)];
        foreach (var position in expected)
        {
            var message = Assert.Single(await reader.ReadNextAsync(1, Token));

            Assert.Equal(position, (message.Group.GroupId, message.Sequence));
        }
        Assert.Empty(await reader.ReadNextAsync(1, Token));
    }
}
