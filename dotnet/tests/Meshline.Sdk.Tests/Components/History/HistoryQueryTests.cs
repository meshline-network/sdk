using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.History;

public sealed class HistoryQueryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ConversationKind.Direct)]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Exclusive_bounds_apply_after_filtering_and_each_batch_is_ascending(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        var id = await SeedAsync(fixture, kind, peer.AccountId);

        Assert.Equal(new[] { 10L, 20L }, await ReadAsync(fixture, kind, id, peer.AccountId, new()));
        Assert.Equal(new[] { 40L, 50L }, await ReadAsync(fixture, kind, id, peer.AccountId, new() { Before = 100 }));
        Assert.Equal(new[] { 20L, 40L }, await ReadAsync(fixture, kind, id, peer.AccountId, new() { After = 10, Before = 50 }));
        Assert.Equal(new[] { 40L, 50L }, await ReadAsync(fixture, kind, id, peer.AccountId, new() { After = 10, Before = 60 }));
        Assert.Empty(await ReadAsync(fixture, kind, id, peer.AccountId, new() { After = 100 }));
    }

    [Theory]
    [InlineData(ConversationKind.Direct, false)]
    [InlineData(ConversationKind.Direct, true)]
    [InlineData(ConversationKind.Group, false)]
    [InlineData(ConversationKind.Group, true)]
    [InlineData(ConversationKind.Channel, false)]
    [InlineData(ConversationKind.Channel, true)]
    public async Task Snapshot_excludes_later_writes_and_canceled_reads_do_not_advance_it(ConversationKind kind, bool backward)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        var id = await SeedAsync(fixture, kind, peer.AccountId);
        var snapshot = await OpenAsync(fixture, kind, id, peer.AccountId, new() { Before = backward ? 100 : null }, Token);
        await using var owner = snapshot.Owner;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot.Read(new CancellationToken(true)));
        Assert.Equal(backward ? [40L, 50L] : new[] { 10L, 20L }, await snapshot.Read(Token));
        await StoredMessageSetup.AddAsync(fixture, kind, id, peer.AccountId, 60, participating: false, Token);
        Assert.Equal(backward ? [10L, 20L] : new[] { 40L, 50L }, await snapshot.Read(Token));
        Assert.Empty(await snapshot.Read(Token));
    }

    [Theory]
    [InlineData(ConversationKind.Direct)]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Opening_captures_range_before_the_caller_can_mutate_it(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        var id = await SeedAsync(fixture, kind, peer.AccountId);
        var range = new HistoryRange { After = 10, Before = 50 };
        var pending = OpenAsync(fixture, kind, id, peer.AccountId, range, Token);
        range.After = -1;
        range.Before = -1;
        var reader = await pending;
        await using var owner = reader.Owner;

        Assert.Equal(new[] { 20L, 40L }, await reader.Read(Token));
    }

    [Theory]
    [InlineData(ConversationKind.Direct, false)]
    [InlineData(ConversationKind.Direct, true)]
    [InlineData(ConversationKind.Group, false)]
    [InlineData(ConversationKind.Group, true)]
    [InlineData(ConversationKind.Channel, false)]
    [InlineData(ConversationKind.Channel, true)]
    public async Task Page_boundaries_continue_across_restart(ConversationKind kind, bool backward)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        var id = await SeedAsync(fixture, kind, peer.AccountId);
        var first = await ReadAsync(fixture, kind, id, peer.AccountId, new() { Before = backward ? 100 : null });
        Assert.Equal(backward ? [40L, 50L] : new[] { 10L, 20L }, first);
        await StoredMessageSetup.AddAsync(fixture, kind, id, peer.AccountId, 60, participating: false, Token);

        await fixture.ReopenAsync();
        var range = new HistoryRange { After = backward ? null : first[^1], Before = backward ? first[0] : null };
        var second = await ReadAsync(fixture, kind, id, peer.AccountId, range);
        Assert.Equal(backward ? [10L, 20L] : new[] { 40L, 50L }, second);
        if (!backward)
            Assert.Equal(new[] { 60L }, await ReadAsync(fixture, kind, id, peer.AccountId, new() { After = second[^1] }));
    }

    [Theory]
    [InlineData(ConversationKind.Direct)]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Opening_honors_cancellation(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        var id = await SeedAsync(fixture, kind, peer.AccountId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenAsync(fixture, kind, id, peer.AccountId, new(), new CancellationToken(true)));
    }

    public static IEnumerable<object?[]> InvalidRanges =>
        from kind in new[] { ConversationKind.Direct, ConversationKind.Group, ConversationKind.Channel }
        from bounds in new (long? After, long? Before)[] { (-1, null), (20, 20), (30, 20), (null, -1), (9_007_199_254_740_992, null) }
        select new object?[] { kind, bounds.After, bounds.Before };

    [Theory]
    [MemberData(nameof(InvalidRanges))]
    public async Task Invalid_bounds_are_rejected(ConversationKind kind, long? after, long? before)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var id = ConversationId(kind, fixture.Account.AccountId);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => OpenAsync(fixture, kind, id, fixture.Account.AccountId, new() { After = after, Before = before }, Token));
    }

    [Theory]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Resource_sequence_bounds_require_a_resource_id(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        if (kind == ConversationKind.Group)
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Client.GroupManager.GetMessagesAsync(null, null, new HistoryRange { After = 1 }, Token));
        else
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Client.ChannelManager.GetPostsAsync(null, null, new HistoryRange { Before = 100 }, Token));
    }

    static string ConversationId(ConversationKind kind, string peer) => kind switch
    {
        ConversationKind.Group => "grp_AAAAAAAAAAAAAAAAAAAAAA",
        ConversationKind.Channel => "chan_AAAAAAAAAAAAAAAAAAAAAA",
        _ => peer
    };

    static async Task<string> SeedAsync(TestClient fixture, ConversationKind kind, string peer)
    {
        await fixture.InitializeAsync();
        using var other = new AccountSigner();
        var id = ConversationId(kind, peer);
        foreach (var sequence in new long[] { 50, 20, 40, 10, 30, 35 })
            await StoredMessageSetup.AddAsync(fixture, kind, id, sequence == 30 ? other.AccountId : peer, sequence, participating: false, Token);

        // Non-direct, unreadable and deleted entries must not consume a visible batch slot.
        await using var database = fixture.Database.Open();
        if (kind == ConversationKind.Direct)
            await database.Messages.Where(value => value.LocalSequence == 35).ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDirect, false), Token);
        else if (kind == ConversationKind.Group)
            await database.GroupEvents.Where(value => value.Sequence == 35).ExecuteUpdateAsync(set => set.SetProperty(value => value.IsMessage, false), Token);
        else
            await database.ChannelPosts.Where(value => value.Sequence == 35).ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDeleted, true), Token);
        fixture.Relay.Handler = (_, _) => throw new InvalidOperationException("Local history must not access the relay.");
        return id;
    }

    static async Task<long[]> ReadAsync(TestClient fixture, ConversationKind kind, string id, string peer, HistoryRange range)
    {
        var reader = await OpenAsync(fixture, kind, id, peer, range, Token);
        await using var owner = reader.Owner;
        return await reader.Read(Token);
    }

    static async Task<(IAsyncDisposable Owner, Func<CancellationToken, Task<long[]>> Read)> OpenAsync(
        TestClient fixture, ConversationKind kind, string id, string peer, HistoryRange range, CancellationToken token)
    {
        if (kind == ConversationKind.Direct)
        {
            var reader = await fixture.Client.MessageManager.GetMessageHistoryAsync(id, range, token);
            return (reader, async ct => (await reader.ReadNextAsync(2, ct)).Select(value => value.LocalSequence).ToArray());
        }
        if (kind == ConversationKind.Group)
        {
            var reader = await fixture.Client.GroupManager.GetMessagesAsync(id, peer, range, token);
            return (reader, async ct => (await reader.ReadNextAsync(2, ct)).Select(value => value.Sequence).ToArray());
        }
        var posts = await fixture.Client.ChannelManager.GetPostsAsync(id, peer, range, token);
        return (posts, async ct => (await posts.ReadNextAsync(2, ct)).Select(value => value.LocalSequence).ToArray());
    }
}
