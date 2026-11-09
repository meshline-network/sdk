using Meshline.Backups;
using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Meshline.Tests.Components.Channels;

public sealed class ChannelBackupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Channel_restore_notifies_initial_follow_state_once_without_descriptor_or_post_events(bool followed)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var record = BackupData.Record("channel", new
        {
            channel_id = "chan_AAAAAAAAAAAAAAAAAAAAAA",
            relay_id = fixture.Relay.RelayId,
            is_followed = followed
        });
        var changes = new List<(object? Sender, ChannelFollowChangedEventArgs Change)>();
        var unrelated = 0;
        fixture.Client.ChannelManager.FollowChanged += (sender, change) => changes.Add((sender, change));
        fixture.Client.ChannelManager.ChannelChanged += (_, _) => unrelated++;
        fixture.Client.ChannelManager.TimelineChanged += (_, _) => unrelated++;
        await fixture.Client.ChannelManager.ImportBackupAsync(Replay([record, record]), Token);
        var notification = Assert.Single(changes);
        Assert.Same(fixture.Client.ChannelManager, notification.Sender);
        Assert.Equal(record.GetProperty("data").GetProperty("channel_id").GetString(), notification.Change.ChannelId);
        Assert.Equal(followed, notification.Change.IsFollowed);
        Assert.Equal(0, unrelated);
    }

    [Fact]
    public async Task Ordinary_channel_change_between_import_records_is_preserved()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var channel = new ChannelRef { ChannelId = "chan_AAAAAAAAAAAAAAAAAAAAAA", RelayId = fixture.Relay.RelayId };
        var record = BackupData.Record("channel",
            JsonSerializer.SerializeToElement(new
            {
                channel_id = channel.ChannelId,
                relay_id = channel.RelayId,
                is_followed = true
            }));
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var notifications = new List<(object? Sender, ChannelFollowChangedEventArgs Change)>();
        fixture.Client.ChannelManager.FollowChanged += (sender, change) => notifications.Add((sender, change));
        var import = fixture.Client.ChannelManager.ImportBackupAsync(Paused(Token), Token);
        if (await Task.WhenAny(entered.Task, import).WaitAsync(Token) == import)
            await import;
        await entered.Task.WaitAsync(Token);
        try
        {
            await fixture.Client.ChannelManager.UnfollowAsync(channel, Token);
        }
        finally { release.TrySetResult(); }
        var report = await import;
        Assert.Equal(1, report.Applied);
        Assert.Equal(1, report.Unchanged);
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, notification =>
        {
            Assert.Same(fixture.Client.ChannelManager, notification.Sender);
            Assert.Equal(channel.ChannelId, notification.Change.ChannelId);
        });
        Assert.True(notifications[0].Change.IsFollowed);
        Assert.False(notifications[1].Change.IsFollowed);
        await using var db = fixture.Database.Open();
        Assert.False((await db.Channels.SingleAsync(Token)).IsFollowed);

        async IAsyncEnumerable<JsonElement> Paused([EnumeratorCancellation] CancellationToken token)
        {
            yield return record;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            yield return record;
        }
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
            yield return record;
        await Task.CompletedTask;
    }
}
