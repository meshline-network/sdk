using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;

namespace Meshline.Tests.Components.Messages;

public sealed class MessageBackupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "incremental backup";
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Fact]
    public async Task Message_component_snapshot_covers_contacts_and_history_while_writes_continue()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        using var later = new AccountSigner();
        await using (var db = source.Database.Open())
        {
            db.Contacts.Add(new()
            {
                AccountId = peer.AccountId,
                State = ContactRelationshipState.Deleted,
                UpdatedAt = Clock.UtcNow
            });
            db.Messages.Add(Message(source, peer.AccountId, "original"));
            await db.SaveChangesAsync(Token);
        }
        await source.Client.StartAsync(Token);
        var records = new List<JsonElement>();
        await using (var reader = source.Client.MessageManager.ExportBackupAsync(BackupSelection.All, Token).GetAsyncEnumerator(Token))
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal("contact", reader.Current.GetProperty("type").GetString());
            records.Add(reader.Current);
            await using (var db = source.Database.Open())
            {
                (await db.Contacts.SingleAsync(Token)).Alias = "changed after snapshot";
                db.Messages.Add(Message(source, later.AccountId, "later"));
                await db.SaveChangesAsync(Token);
            }
            while (await reader.MoveNextAsync())
                records.Add(reader.Current);
        }
        await source.Client.StopAsync(Token);
        await target.Client.MessageManager.ImportBackupAsync(Replay(records), Token);
        await using var check = target.Database.Open();
        Assert.Null((await check.Contacts.SingleAsync(Token)).Alias);
        Assert.Equal(peer.AccountId, (await check.Messages.SingleAsync(Token)).Sender);
        await using var original = source.Database.Open();
        Assert.Equal(2, await original.Messages.CountAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Thousand_messages_resume_from_five_hundred_after_reopen_without_changing_existing_read_state(bool cancel)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        StoredMessageRecord existing;
        await using (var db = source.Database.Open())
        {
            for (var i = 0; i < 1000; i++)
                db.Messages.Add(Message(source, peer.AccountId, "message " + i));
            await db.SaveChangesAsync(Token);
            existing = await db.Messages.AsNoTracking().OrderBy(row => row.LocalSequence).FirstAsync(Token);
        }
        existing.LocalSequence = 0;
        await using (var db = target.Database.Open())
        {
            db.Messages.Add(existing);
            await db.SaveChangesAsync(Token);
        }
        using var output = new MemoryStream();
        await source.Client.ExportBackupAsync(output, Password, new()
        {
            DirectHistory = BackupSelection.All
        }, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var notifications = new List<(object? Sender, MessageInfo Message)>();
        target.Client.MessageManager.MessageReceived += (sender, change) =>
        {
            foreach (var message in change.Messages)
                notifications.Add((sender, message));
        };
        var accountTimelines = 0;
        target.Client.MessageManager.TimelineChanged += (_, _) => accountTimelines++;
        var inserts = 0;
        using (var observer = new DatabaseObserver((name, value) =>
        {
            if (cancel && name.EndsWith("CommandExecuted", StringComparison.Ordinal) && value is CommandExecutedEventData command
                && command.Command.Connection?.DataSource == target.Database.Options.Path
                && command.Command.CommandText.StartsWith("INSERT INTO \"Messages\"", StringComparison.Ordinal)
                && ++inserts == 500)
                cancellation.Cancel();
        }))
        await using (var trigger = cancel ? null : await target.Database.CreateTriggerAsync("stop_at_half",
            "BEFORE INSERT ON Messages WHEN (SELECT COUNT(*) FROM Messages) >= 500 BEGIN SELECT RAISE(ABORT, 'interrupted'); END"))
        {
            output.Position = 0;
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Client.ImportBackupAsync(output, Password, cancellation.Token));
            else
                await Assert.ThrowsAsync<DbUpdateException>(() => target.Client.ImportBackupAsync(output, Password, Token));
        }
        await using (var db = target.Database.Open())
            Assert.Equal(500, await db.Messages.CountAsync(Token));
        Assert.Equal(499, notifications.Count);
        Assert.All(notifications, item => { Assert.Same(target.Client.MessageManager, item.Sender); Assert.Equal(peer.AccountId, item.Message.Key.Sender); Assert.True(item.Message.LocalSequence > 0); });
        Assert.Equal(0, accountTimelines);
        Assert.Equal(500, (await target.Client.GetConversationAsync(peer.AccountId, Token))!.UnreadCount);
        await target.ReopenAsync();
        var received = 0;
        target.Client.MessageManager.MessageReceived += (_, _) => received++;
        target.Client.MessageManager.TimelineChanged += (_, _) => accountTimelines++;
        var requests = target.Relay.Requests.Count;
        output.Position = 0;
        var report = await target.Client.ImportBackupAsync(output, Password, Token);
        Assert.Equal(500, report.Applied);
        Assert.Equal(500, received);
        Assert.Equal(0, accountTimelines);
        Assert.Equal(requests, target.Relay.Requests.Count);
        await using var check = target.Database.Open();
        Assert.Equal(1000, await check.Messages.CountAsync(Token));
        Assert.Equal(1000, (await target.Client.GetConversationAsync(peer.AccountId, Token))!.UnreadCount);
        Assert.Empty(await check.ConversationReads.ToListAsync(Token));
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
            yield return record;
        await Task.CompletedTask;
    }

    static StoredMessageRecord Message(TestClient fixture, string sender, string text) => new()
    {
        Sender = sender,
        Recipient = fixture.Account.AccountId,
        SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
        MessageId = Identifiers.CreateMessageId(),
        CreatedAt = Clock.UtcNow,
        IsDirect = true,
        PayloadType = "meshline.message.direct",
        PayloadJson = new DirectMessage { Body = new() { ContentType = "text/plain", Text = text } }.ToJson()
    };
}
