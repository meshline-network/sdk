using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace Meshline.Tests.Components.Synchronization;

public sealed class ResourceSyncTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Empty_account_timeline_can_be_caught_up_with_a_durable_gap_but_completion_is_runtime_only()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var changes = new ConcurrentQueue<ResourceSyncStatus>();
        manager.SyncStatusChanged += (_, status) => changes.Enqueue(status);
        Assert.Equal(ResourceSyncState.Idle, (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).State);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "message.timeline.sync"
            ? OfflineRelay.Json(new MessageTimelinePage { Items = [], Certificates = [], HasMore = false, HasRetentionGap = true })
            : fixture.Relay.Respond(request));

        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).State == ResourceSyncState.CaughtUp);
        var completed = await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token);
        Assert.True(completed.HasRetentionGap);
        Assert.NotNull(completed.LastSynchronizedAt);
        Assert.Contains(changes, value => value.State == ResourceSyncState.Synchronizing);
        await manager.StopAsync(Token);
        var stopped = await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token);
        Assert.Equal(ResourceSyncState.Idle, stopped.State);
        Assert.Equal(completed.LastSynchronizedAt, stopped.LastSynchronizedAt);
        await fixture.ReopenAsync();
        var restarted = await fixture.Client.MessageManager.GetSyncStatusAsync(fixture.Relay.RelayId, Token);
        Assert.Equal(ResourceSyncState.Idle, restarted.State);
        Assert.True(restarted.HasRetentionGap);
        Assert.Null(restarted.LastSynchronizedAt);
    }

    [Fact]
    public async Task Account_failure_is_observable_and_a_successful_retry_clears_the_block()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var fail = true;
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method != "message.timeline.sync" ? fixture.Relay.Respond(request)
            : fail ? OfflineRelay.Error("forbidden") : OfflineRelay.Json(new MessageTimelinePage { Items = [], Certificates = [], HasMore = false }));
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).State == ResourceSyncState.Blocked);
        var blocked = await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token);
        Assert.Equal(ResourceSyncBlockReason.Permission, blocked.BlockReason);
        Assert.NotNull(blocked.Error);
        Assert.Null(blocked.LastSynchronizedAt);
        fail = false;
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(30));
        await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).State == ResourceSyncState.CaughtUp);
        var recovered = await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token);
        Assert.Null(recovered.BlockReason);
        Assert.Null(recovered.Error);
    }

    [Fact]
    public async Task Group_missing_member_key_blocks_completion_until_readable_ciphertext_is_processed()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new GroupRelay(fixture);
        host.Install();
        var manager = fixture.Client.GroupManager;
        var group = (await manager.CreateGroupAsync(fixture.Relay.RelayId, new() { Name = "sync", MemberCapacity = 4 }, Token)).Ref;
        await manager.SendMessageAsync(group, new() { Body = new() { ContentType = "text/plain", Text = "pending" } }, Token);
        var previous = await manager.GetSyncStatusAsync(group.GroupId, Token);
        Assert.Equal(ResourceSyncState.CaughtUp, previous.State);
        await using var database = fixture.Database.Open();
        var keys = await database.GroupMemberKeys.AsNoTracking().ToListAsync(Token);
        await database.GroupMemberKeys.ExecuteDeleteAsync(Token);
        await database.GroupEpochs.ExecuteUpdateAsync(set => set.SetProperty(value => value.ProtectedApplicationSecret, (byte[]?)null).SetProperty(value => value.ProtectedClientSecret, (byte[]?)null), Token);
        await database.GroupEvents.Where(value => value.MessageId != null).ExecuteUpdateAsync(set => set.SetProperty(value => value.DecryptedPayloadJson, (string?)null).SetProperty(value => value.IsMessage, false), Token);
        var blocked = await manager.SynchronizeAsync(group, Token);
        Assert.Same(blocked, await manager.GetSyncStatusAsync(group.GroupId, Token));
        Assert.Equal(ResourceSyncState.Blocked, blocked.State);
        Assert.Equal(ResourceSyncBlockReason.MissingKey, blocked.BlockReason);
        Assert.Equal(previous.LastSynchronizedAt, blocked.LastSynchronizedAt);

        database.GroupMemberKeys.AddRange(keys);
        await database.SaveChangesAsync(Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = await manager.SynchronizeAsync(group, Token);
        Assert.Equal(ResourceSyncState.CaughtUp, recovered.State);
        Assert.Null(recovered.BlockReason);
        Assert.True(recovered.LastSynchronizedAt > previous.LastSynchronizedAt);
        Assert.True(await database.GroupEvents.AnyAsync(value => value.IsMessage && value.DecryptedPayloadJson != null, Token));
    }

    [Fact]
    public async Task Channel_history_reads_do_not_claim_full_sync_and_followed_sync_clears_errors()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new ChannelRelay(fixture);
        host.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = (await manager.CreateChannelAsync(fixture.Relay.RelayId, "sync", cancellationToken: Token)).Ref;
        await manager.LoadChannelHistoryAsync(channel, cancellationToken: Token);
        Assert.Equal(ResourceSyncState.Idle, (await manager.GetSyncStatusAsync(channel.ChannelId, Token)).State);
        await manager.FollowAsync(channel, Token);
        var fail = true;
        fixture.Relay.Handler = (request, _) => Task.FromResult(fail && request.Method == "channel.read" ? OfflineRelay.Error("forbidden") : host.Respond(request));
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(channel.ChannelId, Token)).State == ResourceSyncState.Blocked);
        Assert.Equal(ResourceSyncBlockReason.Permission, (await manager.GetSyncStatusAsync(channel.ChannelId, Token)).BlockReason);
        fail = false;
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(60));
        await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(channel.ChannelId, Token)).State == ResourceSyncState.CaughtUp);
        await manager.StopAsync(Token);
        Assert.Equal(ResourceSyncState.Idle, (await manager.GetSyncStatusAsync(channel.ChannelId, Token)).State);
    }

}
