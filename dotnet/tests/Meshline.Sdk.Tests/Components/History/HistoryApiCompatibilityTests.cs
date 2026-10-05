using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.History;

public sealed class HistoryApiCompatibilityTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Original_signatures_remain_bindable_to_delegates()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        // Delegate binding requires the original parameter lists, not just optional extra parameters.
        Func<string?, CancellationToken, Task<QueryReader<MessageInfo>>> messages = fixture.Client.MessageManager.GetMessageHistoryAsync;
        Func<string?, string?, CancellationToken, Task<QueryReader<GroupMessageInfo>>> groups = fixture.Client.GroupManager.GetMessagesAsync;
        Func<string?, string?, CancellationToken, Task<QueryReader<ChannelPostInfo>>> channels = fixture.Client.ChannelManager.GetPostsAsync;
        await using var oldMessages = await messages(null, Token);
        await using var oldGroups = await groups(null, null, Token);
        await using var oldChannels = await channels(null, null, Token);
        Assert.Empty(await oldMessages.ReadNextAsync(1, Token));
        Assert.Empty(await oldGroups.ReadNextAsync(1, Token));
        Assert.Empty(await oldChannels.ReadNextAsync(1, Token));

    }

    [Fact]
    public async Task Optional_range_and_cancellation_arguments_remain_callable()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

#pragma warning disable xUnit1051 // These calls deliberately test omitted/default cancellation arguments.
        await using var rangedMessages = await fixture.Client.MessageManager.GetMessageHistoryAsync(null, new HistoryRange());
        await using var rangedGroups = await fixture.Client.GroupManager.GetMessagesAsync(null, null, new HistoryRange());
        await using var rangedChannels = await fixture.Client.ChannelManager.GetPostsAsync(null, null, new HistoryRange());
        Assert.Empty(await rangedMessages.ReadNextAsync(1, Token));
        Assert.Empty(await rangedGroups.ReadNextAsync(1, Token));
        Assert.Empty(await rangedChannels.ReadNextAsync(1, Token));

        await using var namedToken = await fixture.Client.MessageManager.GetMessageHistoryAsync(null, cancellationToken: default);
        Assert.Empty(await namedToken.ReadNextAsync(1, Token));
#pragma warning restore xUnit1051
#pragma warning disable xUnit1051 // Verify source calls that deliberately omit all or some optional arguments.
        await using var noMessageArgs = await fixture.Client.MessageManager.GetMessageHistoryAsync();
        await using var noGroupArgs = await fixture.Client.GroupManager.GetMessagesAsync();
        await using var noChannelArgs = await fixture.Client.ChannelManager.GetPostsAsync();
        await using var peerOnly = await fixture.Client.MessageManager.GetMessageHistoryAsync(fixture.Account.AccountId);
        await using var groupOnly = await fixture.Client.GroupManager.GetMessagesAsync("grp_AAAAAAAAAAAAAAAAAAAAAA");
        await using var channelOnly = await fixture.Client.ChannelManager.GetPostsAsync("chan_AAAAAAAAAAAAAAAAAAAAAA");
        await using var rangeOnly = await fixture.Client.MessageManager.GetMessageHistoryAsync(range: new HistoryRange { After = 0 });
        await using var nullMessages = await fixture.Client.MessageManager.GetMessageHistoryAsync(range: null);
        await using var nullGroups = await fixture.Client.GroupManager.GetMessagesAsync(range: null);
        await using var nullChannels = await fixture.Client.ChannelManager.GetPostsAsync(range: null);
        await using var defaultMessageToken = await fixture.Client.MessageManager.GetMessageHistoryAsync(null, default);
        await using var defaultGroupToken = await fixture.Client.GroupManager.GetMessagesAsync(null, null, default);
        await using var defaultChannelToken = await fixture.Client.ChannelManager.GetPostsAsync(null, null, default);
#pragma warning restore xUnit1051
        Assert.Empty(await noMessageArgs.ReadNextAsync(1, Token));
        Assert.Empty(await noGroupArgs.ReadNextAsync(1, Token));
        Assert.Empty(await noChannelArgs.ReadNextAsync(1, Token));
        Assert.Empty(await peerOnly.ReadNextAsync(1, Token));
        Assert.Empty(await groupOnly.ReadNextAsync(1, Token));
        Assert.Empty(await channelOnly.ReadNextAsync(1, Token));
        Assert.Empty(await rangeOnly.ReadNextAsync(1, Token));
        Assert.Empty(await nullMessages.ReadNextAsync(1, Token));
        Assert.Empty(await nullGroups.ReadNextAsync(1, Token));
        Assert.Empty(await nullChannels.ReadNextAsync(1, Token));

    }
}
