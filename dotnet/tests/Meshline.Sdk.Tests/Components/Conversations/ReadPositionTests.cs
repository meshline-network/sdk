using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Conversations;

public sealed class ReadPositionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interleaved_direct_conversations_keep_independent_read_positions(bool explicitPosition)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var first = new AccountSigner();
        using var second = new AccountSigner();
        await AddMessageAsync(fixture, ConversationKind.Direct, first.AccountId, first.AccountId, 10);
        await AddMessageAsync(fixture, ConversationKind.Direct, second.AccountId, second.AccountId, 20);
        await AddMessageAsync(fixture, ConversationKind.Direct, first.AccountId, first.AccountId, 30);
        await AddMessageAsync(fixture, ConversationKind.Direct, second.AccountId, second.AccountId, 40);

        if (explicitPosition)
            await fixture.Client.MarkReadAsync(first.AccountId, 30, Token);
        else
            await fixture.Client.MarkReadAsync(first.AccountId, Token);
        await fixture.ReopenAsync();
        Assert.Equal(0, (await fixture.Client.GetConversationAsync(first.AccountId, Token))!.UnreadCount);
        Assert.Equal(2, (await fixture.Client.GetConversationAsync(second.AccountId, Token))!.UnreadCount);

        await fixture.Client.MarkReadAsync(second.AccountId, 20, Token);
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(second.AccountId, Token))!.UnreadCount);
        await AddMessageAsync(fixture, ConversationKind.Direct, first.AccountId, first.AccountId, 50);
        await fixture.Client.MarkReadAsync(second.AccountId, Token);
        await using (var reader = await fixture.Client.GetConversationsAsync(new() { UnreadOnly = true }, Token))
        {
            var conversation = Assert.Single(await reader.ReadNextAsync(10, Token));
            Assert.Equal(first.AccountId, conversation.ConversationId);
            Assert.Equal(1, conversation.UnreadCount);
        }
        Assert.Equal(0, (await fixture.Client.GetConversationAsync(second.AccountId, Token))!.UnreadCount);
        await using var database = fixture.Database.Open();
        var positions = await database.ConversationReads.ToDictionaryAsync(value => value.ConversationId, value => value.Sequence, Token);
        Assert.Equal(2, positions.Count);
        Assert.Equal(30, positions[first.AccountId]);
        Assert.Equal(40, positions[second.AccountId]);
    }

    [Theory]
    [InlineData(ConversationKind.Direct)]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Marking_without_a_position_preserves_the_latest_message_and_cancellation_behavior(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var id = ConversationId(kind, peer.AccountId);
        await fixture.Client.MarkReadAsync(id, cancellationToken: Token);
        await using (var database = fixture.Database.Open())
            Assert.Empty(await database.ConversationReads.ToListAsync(Token));

        await AddMessageAsync(fixture, kind, id, peer.AccountId, 10);
        var viewed = await ReadPositionsAsync(fixture, kind, id);
        await AddMessageAsync(fixture, kind, id, peer.AccountId, 20);
        await fixture.Client.MarkReadAsync(id, viewed[0], Token);
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.MarkReadAsync(id, new CancellationToken(true)));
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);

        await fixture.Client.MarkReadAsync(id, Token);
        await fixture.Client.MarkReadAsync(id, viewed[0], Token);
        await fixture.Client.MarkReadAsync(id, Token);
        Assert.Equal(0, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);
        await using (var database = fixture.Database.Open())
            Assert.Equal(20, (await database.ConversationReads.SingleAsync(Token)).Sequence);

        await AddMessageAsync(fixture, kind, id, peer.AccountId, 30);
        await fixture.ReopenAsync();
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);
    }

    [Theory]
    [InlineData(ConversationKind.Direct)]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Explicit_positions_preserve_later_arrivals_and_only_advance_across_restart(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var id = ConversationId(kind, peer.AccountId);
        await AddMessageAsync(fixture, kind, id, peer.AccountId, 10);
        await AddMessageAsync(fixture, kind, id, peer.AccountId, 20);
        var viewed = await ReadPositionsAsync(fixture, kind, id);
        await AddMessageAsync(fixture, kind, id, peer.AccountId, 30);

        await fixture.Client.MarkReadAsync(id, viewed[0], Token);
        Assert.Equal(2, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);
        await fixture.Client.MarkReadAsync(id, viewed[1], Token);
        await fixture.Client.MarkReadAsync(id, viewed[0], Token);
        await fixture.Client.MarkReadAsync(id, viewed[1], Token);
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);

        foreach (var invalid in new[] { -1L, 0, 25, 40, long.MaxValue })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.MarkReadAsync(id, invalid, Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.MarkReadAsync(id, 30, new CancellationToken(true)));
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);

        await fixture.ReopenAsync();
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);
        await Task.WhenAll(fixture.Client.MarkReadAsync(id, 30, Token), fixture.Client.MarkReadAsync(id, 20, Token));
        Assert.Equal(0, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);
        await using var database = fixture.Database.Open();
        Assert.Equal(30, (await database.ConversationReads.SingleAsync(Token)).Sequence);
    }

    [Theory]
    [InlineData(ConversationKind.Direct)]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Unknown_conversation_boundaries_cannot_pre_mark_future_messages(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        using var other = new AccountSigner();
        var id = ConversationId(kind, peer.AccountId);
        var otherId = kind == ConversationKind.Direct ? other.AccountId : id[..^1] + "Q";
        await AddMessageAsync(fixture, kind, otherId, other.AccountId, 10);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.MarkReadAsync(id, 10, Token));
        await AddMessageAsync(fixture, kind, id, peer.AccountId, 20);
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(otherId, Token))!.UnreadCount);
        await using var database = fixture.Database.Open();
        Assert.Empty(await database.ConversationReads.ToListAsync(Token));
    }

    [Fact]
    public async Task A_viewed_channel_publication_remains_a_boundary_after_deletion()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var id = ConversationId(ConversationKind.Channel, peer.AccountId);
        await AddMessageAsync(fixture, ConversationKind.Channel, id, peer.AccountId, 10);
        await AddMessageAsync(fixture, ConversationKind.Channel, id, peer.AccountId, 20);
        var viewed = await ReadPositionsAsync(fixture, ConversationKind.Channel, id);
        await using (var database = fixture.Database.Open())
        {
            var deleted = await database.ChannelPosts.SingleAsync(value => value.Sequence == 20, Token);
            deleted.IsDeleted = true;
            deleted.PostJson = null;
            await database.SaveChangesAsync(Token);
        }
        await fixture.Client.MarkReadAsync(id, Token);
        await using (var database = fixture.Database.Open())
            Assert.Equal(10, (await database.ConversationReads.SingleAsync(Token)).Sequence);
        await AddMessageAsync(fixture, ConversationKind.Channel, id, peer.AccountId, 30);
        await fixture.Client.MarkReadAsync(id, viewed[^1], Token);
        Assert.Equal(1, (await fixture.Client.GetConversationAsync(id, Token))!.UnreadCount);
    }

    static string ConversationId(ConversationKind kind, string peer) => kind switch
    {
        ConversationKind.Group => "grp_AAAAAAAAAAAAAAAAAAAAAA",
        ConversationKind.Channel => "chan_AAAAAAAAAAAAAAAAAAAAAA",
        _ => peer
    };

    static async Task<long[]> ReadPositionsAsync(TestClient fixture, ConversationKind kind, string id)
    {
        if (kind == ConversationKind.Direct)
        {
            await using var reader = await fixture.Client.MessageManager.GetMessageHistoryAsync(id, Token);
            return (await reader.ReadNextAsync(10, Token)).Select(value => value.LocalSequence).ToArray();
        }
        if (kind == ConversationKind.Group)
        {
            await using var reader = await fixture.Client.GroupManager.GetMessagesAsync(id, cancellationToken: Token);
            return (await reader.ReadNextAsync(10, Token)).Select(value => value.LocalSequence).ToArray();
        }
        await using var posts = await fixture.Client.ChannelManager.GetPostsAsync(id, cancellationToken: Token);
        return (await posts.ReadNextAsync(10, Token)).Select(value => value.LocalSequence).ToArray();
    }

    static Task AddMessageAsync(TestClient fixture, ConversationKind kind, string id, string sender, long sequence) =>
        StoredMessageSetup.AddAsync(fixture, kind, id, sender, sequence, participating: true, Token);
}
