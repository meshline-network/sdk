using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace Meshline.Tests.Components.Synchronization;

public sealed class ActiveSynchronizationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Manual_group_sync_consumes_downloaded_private_keys_without_start_and_retries_storage_failure()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new GroupRelay(fixture); host.Install();
        var manager = fixture.Client.GroupManager;
        var group = (await manager.CreateGroupAsync(fixture.Relay.RelayId, new() { Name = "manual recovery", MemberCapacity = 4 }, Token)).Ref;
        await manager.SendMessageAsync(group, new() { Body = new() { ContentType = "text/plain", Text = "recoverable" } }, Token);
        MessageSendRequest shared;
        await using (var database = fixture.Database.Open())
        {
            shared = ProtocolModel.FromJson<MessageSendRequest>((await database.MessageOutbox.SingleAsync(Token)).RequestJson)!;
            await database.GroupMemberKeys.ExecuteDeleteAsync(Token);
            await database.GroupEpochs.ExecuteUpdateAsync(set => set.SetProperty(value => value.ProtectedClientSecret, (byte[]?)null).SetProperty(value => value.ProtectedApplicationSecret, (byte[]?)null), Token);
            await database.GroupEvents.Where(value => value.MessageId != null).ExecuteUpdateAsync(set => set.SetProperty(value => value.DecryptedPayloadJson, (string?)null).SetProperty(value => value.IsMessage, false), Token);
        }
        Assert.Equal(ResourceSyncBlockReason.MissingKey, (await manager.SynchronizeAsync(group, Token)).BlockReason);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "message.timeline.sync"
            ? OfflineRelay.Json(new MessageTimelinePage
            {
                Items = [new() { Sequence = 0, Envelope = shared.Envelope, KeyBox = shared.RecipientBoxes[0], AcceptedAt = Clock.UtcNow.ToUnixTimeSeconds() }],
                Certificates = [fixture.Client.Device!],
                HasMore = false
            }) : host.Respond(request));
        await fixture.Client.MessageManager.SynchronizeAsync(fixture.Relay.RelayId, Token);
        await using (var database = fixture.Database.Open())
        {
            Assert.True(await database.Messages.AnyAsync(value => value.PayloadType == "meshline.account.group.state.sync", Token));
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_manual_key BEFORE INSERT ON GroupMemberKeys BEGIN SELECT RAISE(ABORT, 'injected storage failure'); END;", Token);
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => manager.SynchronizeAsync(group, Token));
        Assert.Equal(ResourceSyncBlockReason.Storage, (await manager.GetSyncStatusAsync(group.GroupId, Token)).BlockReason);
        await using (var database = fixture.Database.Open())
        {
            Assert.Empty(await database.GroupMemberKeys.ToListAsync(Token));
            Assert.False(await database.GroupAccountMessageCursors.AnyAsync(value => value.LocalSequence > 0, Token));
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_manual_key;", Token);
        }
        var completed = await manager.SynchronizeAsync(group, Token);
        Assert.Equal(ResourceSyncState.CaughtUp, completed.State);
        Assert.Same(completed, await manager.GetSyncStatusAsync(group.GroupId, Token));
        await using (var database = fixture.Database.Open())
            Assert.True(await database.GroupEvents.AnyAsync(value => value.IsMessage && value.DecryptedPayloadJson != null, Token));
        Assert.Equal(ComponentState.Stopped, manager.LifecycleState);
        Assert.Equal(ComponentState.Stopped, fixture.Client.MessageManager.LifecycleState);
    }

    [Theory]
    [InlineData(ConversationKind.Direct)]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Stopping_background_work_preserves_manual_completion_status_and_events(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        Func<Task<ResourceSyncStatus>> synchronize;
        Func<Task<ResourceSyncStatus>> status;
        Func<Task> start;
        Func<Task> stop;
        Func<ObservedRequest, HttpResponseMessage> respond = request => fixture.Relay.Respond(request);
        var changes = new ConcurrentQueue<ResourceSyncStatus>();
        string method;
        if (kind == ConversationKind.Direct)
        {
            var manager = fixture.Client.MessageManager;
            method = "message.timeline.sync";
            synchronize = () => manager.SynchronizeAsync(fixture.Relay.RelayId, Token);
            status = () => manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token);
            start = () => manager.StartAsync(Token); stop = () => manager.StopAsync(Token);
            manager.SyncStatusChanged += (_, value) => changes.Enqueue(value);
        }
        else if (kind == ConversationKind.Group)
        {
            var host = new GroupRelay(fixture); host.Install(); respond = host.Respond;
            var manager = fixture.Client.GroupManager;
            var group = (await manager.CreateGroupAsync(fixture.Relay.RelayId, new() { Name = "stop", MemberCapacity = 4 }, Token)).Ref;
            method = "group.sync";
            synchronize = () => manager.SynchronizeAsync(group, Token);
            status = () => manager.GetSyncStatusAsync(group.GroupId, Token);
            start = () => manager.StartAsync(Token); stop = () => manager.StopAsync(Token);
            manager.SyncStatusChanged += (_, value) => changes.Enqueue(value);
        }
        else
        {
            var host = new ChannelRelay(fixture); host.Install(); respond = host.Respond;
            var manager = fixture.Client.ChannelManager;
            var channel = (await manager.CreateChannelAsync(fixture.Relay.RelayId, "stop", cancellationToken: Token)).Ref;
            await manager.FollowAsync(channel, Token);
            method = "channel.read";
            synchronize = () => manager.SynchronizeAsync(channel, Token);
            status = () => manager.GetSyncStatusAsync(channel.ChannelId, Token);
            start = () => manager.StartAsync(Token); stop = () => manager.StopAsync(Token);
            manager.SyncStatusChanged += (_, value) => changes.Enqueue(value);
        }
        await start();
        await AsyncTest.UntilAsync(async () => (await status()).State == ResourceSyncState.CaughtUp);
        var previous = await status();
        var entered = AsyncTest.Signal(); var release = AsyncTest.Signal();
        var manual = new AsyncLocal<bool>();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == method && manual.Value) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return respond(request);
        };
        manual.Value = true;
        var foreground = synchronize();
        manual.Value = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await stop().WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(ResourceSyncState.Synchronizing, (await status()).State);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1)); changes.Clear(); release.TrySetResult();
            var completed = await foreground.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(ResourceSyncState.CaughtUp, completed.State);
            Assert.True(completed.LastSynchronizedAt > previous.LastSynchronizedAt);
            Assert.Same(completed, await status());
            Assert.Same(completed, Assert.Single(changes));
        }
        finally { release.TrySetResult(); await foreground; await stop(); }
    }

    [Theory]
    [InlineData(ConversationKind.Direct)]
    [InlineData(ConversationKind.Group)]
    [InlineData(ConversationKind.Channel)]
    public async Task Foreground_passes_are_fresh_without_start_and_propagate_failures(ConversationKind kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        Func<Task<ResourceSyncStatus>> synchronize;
        Func<Task<ResourceSyncStatus>> status;
        Func<ObservedRequest, HttpResponseMessage> respond = request => fixture.Relay.Respond(request);
        string method;
        if (kind == ConversationKind.Direct)
        {
            method = "message.timeline.sync";
            synchronize = () => fixture.Client.MessageManager.SynchronizeAsync(fixture.Relay.RelayId, Token);
            status = () => fixture.Client.MessageManager.GetSyncStatusAsync(fixture.Relay.RelayId, Token);
        }
        else if (kind == ConversationKind.Group)
        {
            var host = new GroupRelay(fixture); host.Install();
            var group = (await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new() { Name = "active", MemberCapacity = 4 }, Token)).Ref;
            respond = host.Respond;
            method = "group.sync";
            synchronize = () => fixture.Client.GroupManager.SynchronizeAsync(group, Token);
            status = () => fixture.Client.GroupManager.GetSyncStatusAsync(group.GroupId, Token);
        }
        else
        {
            var host = new ChannelRelay(fixture); host.Install();
            var channel = (await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "active", cancellationToken: Token)).Ref;
            respond = host.Respond;
            method = "channel.read";
            synchronize = () => fixture.Client.ChannelManager.SynchronizeAsync(channel, Token);
            status = () => fixture.Client.ChannelManager.GetSyncStatusAsync(channel.ChannelId, Token);
        }
        var reads = 0;
        var fail = false;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == method) { reads++; if (fail) return Task.FromResult(OfflineRelay.Error("forbidden")); }
            return Task.FromResult(respond(request));
        };
        Assert.Equal(ResourceSyncState.CaughtUp, (await synchronize()).State);
        var before = reads;
        Assert.Equal(ResourceSyncState.CaughtUp, (await synchronize()).State);
        Assert.True(reads > before);
        var completed = await status();
        fail = true;
        await Assert.ThrowsAsync<RelayException>(synchronize);
        Assert.Equal(ResourceSyncState.Blocked, (await status()).State);
        Assert.Equal(ResourceSyncBlockReason.Permission, (await status()).BlockReason);
        Assert.Equal(completed.LastSynchronizedAt, (await status()).LastSynchronizedAt);
        fail = false;
        Assert.Equal(ResourceSyncState.CaughtUp, (await synchronize()).State);
        Assert.Null((await status()).Error);
        Assert.Equal(ComponentState.Stopped, fixture.Client.LifecycleState);
        await using var database = fixture.Database.Open();
        Assert.False(await database.Channels.AnyAsync(value => value.IsFollowed, Token));
    }

    [Fact]
    public async Task Foreground_message_sync_queues_behind_background_and_canceled_waiters_do_not_run()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var requests = 0;
        var active = 0;
        var overlap = false;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method != "message.timeline.sync") return fixture.Relay.Respond(request);
            if (Interlocked.Increment(ref active) > 1) overlap = true;
            try
            {
                if (Interlocked.Increment(ref requests) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
                return OfflineRelay.Json(new MessageTimelinePage { Items = [], Certificates = [], HasMore = false });
            }
            finally { Interlocked.Decrement(ref active); }
        };
        var manager = fixture.Client.MessageManager;
        await manager.StartAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var cancellation = new CancellationTokenSource();
            var canceled = manager.SynchronizeAsync(fixture.Relay.RelayId, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(5), Token));
            var foreground = manager.SynchronizeAsync(fixture.Relay.RelayId, Token);
            Assert.False(foreground.IsCompleted);
            Assert.Equal(1, requests);
            release.TrySetResult();
            Assert.Equal(ResourceSyncState.CaughtUp, (await foreground.WaitAsync(TimeSpan.FromSeconds(10), Token)).State);
            // Socket/route notifications can queue another legitimate background pass.
            Assert.True(requests >= 2);
            Assert.False(overlap);
        }
        finally { release.TrySetResult(); await manager.StopAsync(Token); }
    }

    [Fact]
    public async Task Cancellation_keeps_committed_pages_and_next_pass_resumes_from_their_cursor()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var queued = await manager.SendMessageAsync(fixture.Account.AccountId, new() { Body = new() { ContentType = "text/plain", Text = "committed" } }, Token);
        MessageSendRequest sent;
        await using (var database = fixture.Database.Open())
        {
            sent = ProtocolModel.FromJson<MessageSendRequest>((await database.MessageOutbox.FindAsync([queued.MessageId], Token))!.RequestJson)!;
            await database.Messages.ExecuteDeleteAsync(Token);
        }
        var afters = new List<long>();
        using var cancellation = new CancellationTokenSource();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method != "message.timeline.sync") return fixture.Relay.Respond(request);
            var after = long.Parse(System.Web.HttpUtility.ParseQueryString(request.Query)["after"]!);
            afters.Add(after);
            if (after < 0) return OfflineRelay.Json(new MessageTimelinePage
            {
                Items = [new() { Sequence = 0, Envelope = sent.Envelope, KeyBox = sent.RecipientBoxes[0], AcceptedAt = Clock.UtcNow.ToUnixTimeSeconds() }],
                Certificates = [fixture.Client.Device!],
                HasMore = true
            });
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SynchronizeAsync(fixture.Relay.RelayId, cancellation.Token));
        Assert.Equal(ResourceSyncState.Idle, (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).State);
        Assert.NotNull(await manager.GetMessageAsync(new() { Sender = fixture.Account.AccountId, MessageId = queued.MessageId }, Token));
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "message.timeline.sync") return Task.FromResult(fixture.Relay.Respond(request));
            afters.Add(long.Parse(System.Web.HttpUtility.ParseQueryString(request.Query)["after"]!));
            return Task.FromResult(OfflineRelay.Json(new MessageTimelinePage { Items = [], Certificates = [], HasMore = false }));
        };
        Assert.Equal(ResourceSyncState.CaughtUp, (await manager.SynchronizeAsync(fixture.Relay.RelayId, Token)).State);
        Assert.Equal([-1L, 0, 0], afters);
    }
}
