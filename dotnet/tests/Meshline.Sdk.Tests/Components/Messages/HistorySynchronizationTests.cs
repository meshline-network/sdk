using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Messages;

public sealed class HistorySynchronizationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Timeline_commits_history_without_completing_the_send()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient(); await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var status = await manager.SendMessageAsync(fixture.Account.AccountId, new() { Body = new() { ContentType = "text/plain", Text = "history" } }, Token);
        var outgoing = await manager.GetOutgoingMessageAsync(status.MessageId, Token);
        Assert.Equal("history", outgoing!.Message.Body!.Text);
        Assert.Equal(MessageSendState.Queued, outgoing.Status.State);
        MessageSendRequest request;
        await using (var db = fixture.Database.Open())
        {
            Assert.Empty(await db.Messages.ToListAsync(Token));
            request = ProtocolModel.FromJson<MessageSendRequest>((await db.MessageOutbox.FindAsync([status.MessageId], Token))!.RequestJson)!;
        }
        var received = 0; manager.MessageReceived += (_, args) => received += args.Messages.Count;
        fixture.Relay.Handler = (http, _) => Task.FromResult(http.Method == "message.timeline.sync" ? OfflineRelay.Json(new MessageTimelinePage
        {
            Items = long.Parse(RequestQuery.Parse(http)["after"]) < 5 ? [new() { Sequence = 5, AcceptedAt = request.Envelope.CreatedAt, Envelope = request.Envelope, KeyBox = request.RecipientBoxes[0] }] : [],
            Certificates = [fixture.Client.Device!],
            HasMore = false
        }) : fixture.Relay.Respond(http));
        await manager.SynchronizeAsync(fixture.Relay.RelayId, Token);
        await manager.SynchronizeAsync(fixture.Relay.RelayId, Token);
        Assert.Equal(1, received);
        Assert.Equal(MessageSendState.Queued, (await manager.GetSendStatusAsync(status.MessageId, Token))!.State);
        await using var check = fixture.Database.Open();
        var history = await check.Messages.SingleAsync(Token);
        Assert.Equal(status.MessageId, history.MessageId);
        Assert.Equal("history", ProtocolModel.FromJson<DirectMessage>(history.PayloadJson!)!.Body!.Text);
        Assert.Equal(5, (await check.AccountTimelines.SingleAsync(Token)).Sequence);
    }

    [Fact]
    public async Task Accepted_terminal_outbox_survives_retention_until_history_is_synchronized()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient(); await fixture.InitializeAsync();
        string[] protectedIds = [Identifiers.CreateMessageId(), Identifiers.CreateMessageId()];
        await using (var db = fixture.Database.Open())
        {
            foreach (var (id, index) in protectedIds.Select((id, index) => (id, index)))
                db.MessageOutbox.Add(Row(id, index == 0 ? MessageSendState.TargetAccepted : MessageSendState.Failed, Clock.UtcNow));
            for (var index = 0; index < 1001; index++) db.MessageOutbox.Add(Row(Identifiers.CreateMessageId(), MessageSendState.Canceled, null));
            await db.SaveChangesAsync(Token);
        }
        await fixture.ReopenAsync();
        await using var check = fixture.Database.Open();
        Assert.Equal(1002, await check.MessageOutbox.CountAsync(Token));
        foreach (var id in protectedIds) Assert.NotNull(await check.MessageOutbox.FindAsync([id], Token));

        MessageOutboxRecord Row(string id, MessageSendState state, DateTimeOffset? accepted) => new()
        {
            MessageId = id,
            Recipient = fixture.Account.AccountId,
            RelayId = fixture.Relay.RelayId,
            RequestJson = "{}",
            IsDirect = true,
            CreatedAt = Clock.UtcNow,
            NextAttemptAt = Clock.UtcNow,
            State = state,
            AcceptedAt = accepted
        };
    }
}
