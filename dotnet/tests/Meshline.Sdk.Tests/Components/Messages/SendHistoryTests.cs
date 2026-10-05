using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Messages;

public sealed class SendHistoryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string AcceptedId = "msg_AAECAwQFBgcICQoLDA0ODw";
    const string FailedId = "msg_EBESExQVFhcYGRobHB0eHw";
    const string CanceledId = "msg_ICEiIyQlJicoKSorLC0uLw";

    [Fact]
    public async Task Initialization_limits_terminal_history_without_pruning_pending_work()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.Database.MigrateAsync();
        var pending = new[] { MessageSendState.Queued, MessageSendState.Submitting, MessageSendState.SubmissionUnknown, MessageSendState.RelayAccepted }
            .ToDictionary(state => Identifiers.CreateMessageId(), state => state);
        await using (var database = fixture.Database.Open())
        {
            database.MessageOutbox.AddRange(
                Record(fixture, AcceptedId, MessageSendState.TargetAccepted, 0),
                Record(fixture, FailedId, MessageSendState.Failed, 10),
                Record(fixture, CanceledId, MessageSendState.Canceled, 10));
            for (var index = 0; index < 999; index++)
            {
                var record = Record(fixture, Identifiers.CreateMessageId(), MessageSendState.Canceled, 100);
                record.IsDirect = index != 0;
                database.MessageOutbox.Add(record);
            }
            foreach (var (id, state) in pending) database.MessageOutbox.Add(Record(fixture, id, state, -100));
            await database.SaveChangesAsync(Token);
        }
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        foreach (var (id, state) in pending)
            Assert.Equal(state == MessageSendState.Submitting ? MessageSendState.SubmissionUnknown : state, (await manager.GetSendStatusAsync(id, Token))!.State);
        await using var history = await manager.GetOutboxAsync(state: MessageSendState.TargetAccepted | MessageSendState.Failed | MessageSendState.Canceled, cancellationToken: Token);
        var retained = await history.ReadNextAsync(1100, Token);
        Assert.Equal(999, retained.Count);
        Assert.Equal(CanceledId, retained[0].MessageId);
        Assert.Null(await manager.GetSendStatusAsync(Identifiers.CreateMessageId(), Token));
        Assert.Null(await manager.GetSendStatusAsync(AcceptedId, Token));
        Assert.Null(await manager.GetSendStatusAsync(FailedId, Token));
        await using var check = fixture.Database.Open();
        Assert.Equal(1004, await check.MessageOutbox.CountAsync(Token));
    }

    [Fact]
    public async Task Completion_atomically_prunes_by_creation_order_and_retains_message_history()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var old = await manager.SendMessageAsync(fixture.Account.AccountId, new() { Body = new() { ContentType = "text/plain", Text = "completed later" } }, Token);
        await using (var database = fixture.Database.Open())
        {
            database.MessageOutbox.Add(Record(fixture, AcceptedId, MessageSendState.TargetAccepted, 50));
            for (var index = 0; index < 999; index++)
                database.MessageOutbox.Add(Record(fixture, Identifiers.CreateMessageId(), MessageSendState.Canceled, 100));
            await database.SaveChangesAsync(Token);
        }
        var canceled = new List<string>();
        var waiting = manager.WaitForSendStatusAsync(old.MessageId, cancellationToken: Token);
        manager.SendStatusChanged += (_, change) => { if (change.Status.State == MessageSendState.Canceled) canceled.Add(change.Status.MessageId); };
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_history_cleanup BEFORE DELETE ON MessageOutbox BEGIN SELECT RAISE(ABORT, 'cleanup unavailable'); END", Token);
        await Assert.ThrowsAsync<SqliteException>(() => manager.CancelMessageAsync(old.MessageId, Token));
        Assert.Equal(MessageSendState.Queued, (await manager.GetSendStatusAsync(old.MessageId, Token))!.State);
        Assert.Empty(canceled);
        Assert.False(waiting.IsCompleted);
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_history_cleanup", Token);
        Assert.True(await manager.CancelMessageAsync(old.MessageId, Token));
        Assert.Equal(MessageSendState.Canceled, (await waiting.WaitAsync(TimeSpan.FromSeconds(10), Token))!.State);
        Assert.Null(await manager.GetSendStatusAsync(old.MessageId, Token));
        Assert.Null(await manager.WaitForSendStatusAsync(old.MessageId, cancellationToken: Token));
        Assert.Equal(MessageSendState.TargetAccepted, (await manager.GetSendStatusAsync(AcceptedId, Token))!.State);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(200));
        var recent = await manager.SendMessageAsync(fixture.Account.AccountId, new() { Body = new() { ContentType = "text/plain", Text = "recent" } }, Token);
        Assert.True(await manager.CancelMessageAsync(recent.MessageId, Token));
        Assert.Equal([old.MessageId, recent.MessageId], canceled);
        Assert.Null(await manager.GetSendStatusAsync(AcceptedId, Token));
        await using (var messages = await manager.GetMessageHistoryAsync(fixture.Account.AccountId, Token))
            Assert.Equal(2, (await messages.ReadNextAsync(10, Token)).Count);

        await fixture.ReopenAsync();
        Assert.Equal(MessageSendState.Canceled, (await fixture.Client.MessageManager.GetSendStatusAsync(recent.MessageId, Token))!.State);
        await using var check = fixture.Database.Open();
        Assert.Equal(1000, await check.MessageOutbox.CountAsync(Token));
    }

    static MessageOutboxRecord Record(TestClient fixture, string id, MessageSendState state, int seconds) => new()
    {
        MessageId = id,
        Recipient = fixture.Account.AccountId,
        CreatedAt = Clock.UtcNow.AddSeconds(seconds),
        IsDirect = true,
        State = state,
        RelayId = fixture.Relay.RelayId,
        RequestJson = "{}",
        AcceptedAt = state is MessageSendState.RelayAccepted or MessageSendState.TargetAccepted ? Clock.UtcNow : null,
        NextAttemptAt = Clock.UtcNow
    };
}
