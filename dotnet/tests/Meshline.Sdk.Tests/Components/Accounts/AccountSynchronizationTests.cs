using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountSynchronizationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contact_sync_deduplicates_and_does_not_restore_stale_relationships(bool deleted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        await ClearOutboxAsync(fixture);
        await fixture.Client.MessageManager.SetAliasAsync(peer.AccountId, "older", Token);
        var older = await TakeOutboxAsync(fixture);
        if (deleted)
            await fixture.Client.MessageManager.RemoveContactAsync(peer.AccountId, Token);
        else
            await fixture.Client.MessageManager.SetAliasAsync(peer.AccountId, "newer", Token);
        var latest = await TakeOutboxAsync(fixture);
        // Recreate the receiving device's unsynchronized local contact view.
        await using (var db = fixture.Database.Open())
            await db.Contacts.ExecuteDeleteAsync(Token);
        InstallTimeline(fixture, [latest, older, latest], request => fixture.Relay.Respond(request));
        await fixture.Client.MessageManager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () =>
        {
            await using var db = fixture.Database.Open();
            return await db.AccountTimelines.AnyAsync(row => row.Sequence == 2, Token);
        });
        await fixture.Client.MessageManager.StopAsync(Token);

        await fixture.ReopenAsync();
        var contact = await fixture.Client.MessageManager.GetContactAsync(peer.AccountId, Token);
        if (deleted)
            Assert.Null(contact);
        else
            Assert.Equal("newer", contact!.Alias);
        await using var verify = fixture.Database.Open();

        Assert.Equal(2, await verify.Messages.CountAsync(Token));
        Assert.Equal(deleted ? ContactRelationshipState.Deleted : ContactRelationshipState.Active, (await verify.Contacts.SingleAsync(Token)).State);
    }

    [Fact]
    public async Task Group_private_state_and_consumer_cursor_retry_atomically_after_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new()
        {
            Name = "recover",
            MemberCapacity = 10,
            InvitePolicy = GroupInvitePolicy.Administrators
        }, Token);
        var sharedState = await TakeOutboxAsync(fixture);
        InstallTimeline(fixture, [sharedState], relay.Respond);
        await fixture.Client.MessageManager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () =>
        {
            await using var db = fixture.Database.Open();
            return await db.Messages.AnyAsync(row => row.PayloadType == "meshline.account.group.state.sync", Token);
        });
        await fixture.Client.MessageManager.StopAsync(Token);
        await using (var db = fixture.Database.Open())
        {
            await db.GroupMemberKeys.ExecuteDeleteAsync(Token);
            await db.GroupEpochs.ExecuteUpdateAsync(set => set.SetProperty(row => row.ProtectedClientSecret, (byte[]?)null).SetProperty(row => row.ProtectedApplicationSecret, (byte[]?)null), Token);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_member_key BEFORE INSERT ON GroupMemberKeys BEGIN SELECT RAISE(ABORT, 'injected storage failure'); END;", Token);
        }

        var failed = AsyncTest.Signal();
        fixture.Client.GroupManager.BackgroundError += (_, error) =>
        {
            if (error.Error is DbUpdateException)
                failed.TrySetResult();
            else
                failed.TrySetException(error.Error);
        };
        await fixture.Client.GroupManager.StartAsync(Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.GroupManager.StopAsync(Token);
        await using (var db = fixture.Database.Open())
        {
            Assert.Empty(await db.GroupMemberKeys.ToListAsync(Token));
            Assert.False(await db.GroupAccountMessageCursors.AnyAsync(row => row.LocalSequence > 0, Token));

            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_member_key;", Token);
        }

        await fixture.ReopenAsync();
        await fixture.Client.GroupManager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () =>
        {
            await using var db = fixture.Database.Open();
            return await db.GroupEpochs.AnyAsync(row => row.ProtectedApplicationSecret != null, Token);
        });
        await fixture.Client.GroupManager.StopAsync(Token);
        await using var verify = fixture.Database.Open();

        Assert.Single(await verify.GroupMemberKeys.ToListAsync(Token));
        Assert.True((await verify.GroupAccountMessageCursors.SingleAsync(Token)).LocalSequence > 0);
        Assert.Equal(group.Ref.GroupId, (await verify.GroupMemberKeys.SingleAsync(Token)).GroupId);
    }

    static async Task ClearOutboxAsync(TestClient fixture)
    {
        await using var db = fixture.Database.Open();
        await db.MessageOutbox.ExecuteDeleteAsync(Token);
    }

    static async Task<MessageSendRequest> TakeOutboxAsync(TestClient fixture)
    {
        await using var db = fixture.Database.Open();
        var row = Assert.Single(await db.MessageOutbox.ToListAsync(Token));
        var request = ProtocolModel.FromJson<MessageSendRequest>(row.RequestJson)!;
        await db.MessageOutbox.ExecuteDeleteAsync(Token);
        return request;
    }

    static void InstallTimeline(TestClient fixture, MessageSendRequest[] requests, Func<ObservedRequest, HttpResponseMessage> other)
    {
        var entries = requests.Select((request, index) => new MessageTimelineEntry
        {
            Sequence = index,
            Envelope = request.Envelope,
            KeyBox = request.RecipientBoxes[0],
            AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds()
        }).ToArray();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "message.timeline.sync" ? OfflineRelay.Json(new MessageTimelinePage
        {
            Items = [.. entries.Where(entry => entry.Sequence > long.Parse(RequestQuery.Parse(request)["after"]))],
            Certificates = [fixture.Client.Device!],
            HasMore = false
        }) : other(request));
    }
}
