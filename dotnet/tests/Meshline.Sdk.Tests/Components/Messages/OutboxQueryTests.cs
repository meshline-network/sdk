using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Tests.Components.Messages;

public sealed class OutboxQueryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Content_and_status_survive_restart_without_creating_history_or_network_work()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var attachment = new ContentReference
        {
            Uri = "https://files.example.test/attachment",
            Hash = "sha256:" + new string('A', 43),
            ContentType = "application/octet-stream",
            Size = 3
        };
        var reply = new MessageRef { Sender = fixture.Account.AccountId, MessageId = Identifiers.CreateMessageId() };
        var queued = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, new()
        {
            Body = new() { ContentType = "text/plain", Text = "outgoing content" },
            Attachments = [attachment],
            ReplyTo = reply
        }, Token);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt != 0) await fixture.ReopenAsync();
            var requests = fixture.Relay.Requests.Count;
            await using var reader = await fixture.Client.MessageManager.GetOutboxAsync(cancellationToken: Token);
            var item = Assert.Single(await reader.ReadNextAsync(10, Token));
            Assert.Equal("outgoing content", item.Message.Body!.Text);
            Assert.Equal("text/plain", item.Message.Body.ContentType);
            Assert.Equal(attachment, Assert.Single(item.Message.Attachments!.Value));
            Assert.Equal(reply.Sender, item.Message.ReplyTo!.From);
            Assert.Equal(reply.MessageId, item.Message.ReplyTo.MessageId);
            Assert.Equal(queued.MessageId, item.Status.MessageId);
            Assert.Equal(queued.Recipient, item.Status.Recipient);
            Assert.Equal(queued.CreatedAt, item.Status.CreatedAt);
            Assert.Equal(MessageSendState.Queued, item.Status.State);
            Assert.Null(item.Status.AcceptedRelayId);
            Assert.Null(item.Status.AcceptedAt);
            Assert.Null(item.Status.ErrorMessage);
            AssertSame(item, await fixture.Client.MessageManager.GetOutgoingMessageAsync(queued.MessageId, Token));
            Assert.Empty(await reader.ReadNextAsync(10, Token));
            Assert.Equal(requests, fixture.Relay.Requests.Count);
            await using var database = fixture.Database.Open();
            Assert.Empty(await database.Messages.ToListAsync(Token));
        }
    }

    [Fact]
    public async Task Filters_and_pages_direct_content_with_all_retained_states_in_creation_order()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        MessageSendState[] states = [MessageSendState.Queued, MessageSendState.Submitting, MessageSendState.SubmissionUnknown,
            MessageSendState.RelayAccepted, MessageSendState.TargetAccepted, MessageSendState.Failed, MessageSendState.Canceled];
        var rows = states.Select((state, index) =>
        {
            var row = Record(fixture, index % 2 == 0 ? peer.AccountId : fixture.Account.AccountId, state, state.ToString());
            row.CreatedAt = Clock.UtcNow.AddSeconds(index / 2);
            row.AcceptedAt = state is MessageSendState.RelayAccepted or MessageSendState.TargetAccepted or MessageSendState.Failed ? Clock.UtcNow : null;
            row.ErrorMessage = state == MessageSendState.Failed ? "delivery rejected" : null;
            return row;
        }).ToArray();
        await using (var database = fixture.Database.Open())
        {
            database.MessageOutbox.AddRange(rows.Reverse());
            var internalMessage = Record(fixture, peer.AccountId, MessageSendState.Queued, "internal");
            internalMessage.IsDirect = false;
            internalMessage.PayloadJson = "not direct-message JSON";
            database.MessageOutbox.Add(internalMessage);
            await database.SaveChangesAsync(Token);
        }
        var requests = fixture.Relay.Requests.Count;
        var ordered = rows.OrderBy(row => row.CreatedAt).ThenBy(row => row.MessageId, StringComparer.Ordinal).ToArray();
        await using (var reader = await fixture.Client.MessageManager.GetOutboxAsync(cancellationToken: Token))
        {
            var actual = new List<OutgoingMessageInfo>();
            while (true)
            {
                var batch = await reader.ReadNextAsync(2, Token);
                if (batch.Count == 0) break;
                actual.AddRange(batch);
            }
            Assert.Equal(ordered.Select(row => row.MessageId), actual.Select(item => item.Status.MessageId));
            foreach (var (row, item) in ordered.Zip(actual))
            {
                Assert.Equal(row.State.ToString(), item.Message.Body!.Text);
                Assert.Equal(row.State, item.Status.State);
                Assert.Equal(row.Recipient, item.Status.Recipient);
                Assert.Equal(row.CreatedAt, item.Status.CreatedAt);
                Assert.Equal(row.AcceptedAt, item.Status.AcceptedAt);
                Assert.Equal(row.AcceptedAt is null ? null : row.RelayId, item.Status.AcceptedRelayId);
                Assert.Equal(row.ErrorMessage, item.Status.ErrorMessage);
                AssertSame(item, await fixture.Client.MessageManager.GetOutgoingMessageAsync(row.MessageId, Token));
            }
        }
        const MessageSendState filter = MessageSendState.Queued | MessageSendState.Canceled | MessageSendState.TargetAccepted | MessageSendState.Failed;
        await using (var reader = await fixture.Client.MessageManager.GetOutboxAsync(peer.AccountId, filter, Token))
        {
            var actual = await reader.ReadNextAsync(10, Token);
            Assert.Equal(ordered.Where(row => row.Recipient == peer.AccountId && (row.State & filter) != 0).Select(row => row.MessageId),
                actual.Select(item => item.Status.MessageId));
        }
        await using (var reader = await fixture.Client.MessageManager.GetOutboxAsync(state: MessageSendState.None, cancellationToken: Token))
            Assert.Empty(await reader.ReadNextAsync(10, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.MessageManager.GetOutboxAsync(state: (MessageSendState)128, cancellationToken: Token));
        Assert.Equal(requests, fixture.Relay.Requests.Count);
    }

    [Fact]
    public async Task Open_snapshot_preserves_content_and_status_across_changes_eviction_and_new_sends()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var first = await manager.SendMessageAsync(fixture.Account.AccountId, Draft("first"), Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        var evicted = await manager.SendMessageAsync(fixture.Account.AccountId, Draft("evicted"), Token);
        Assert.True(await manager.CancelMessageAsync(evicted.MessageId, Token));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        var pending = await manager.SendMessageAsync(fixture.Account.AccountId, Draft("pending"), Token);
        await using var snapshot = await manager.GetOutboxAsync(cancellationToken: Token);
        Assert.Equal(first.MessageId, Assert.Single(await snapshot.ReadNextAsync(1, Token)).Status.MessageId);

        Assert.True(await manager.CancelMessageAsync(pending.MessageId, Token));
        var changed = await manager.GetOutgoingMessageAsync(pending.MessageId, Token);
        Assert.Equal(MessageSendState.Canceled, changed!.Status.State);
        Assert.Equal("pending", changed.Message.Body!.Text);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await using (var database = fixture.Database.Open())
        {
            for (var index = 0; index < 1000; index++)
            {
                var row = Record(fixture, fixture.Account.AccountId, MessageSendState.Canceled, "internal");
                row.IsDirect = false;
                row.PayloadJson = null;
                database.MessageOutbox.Add(row);
            }
            await database.SaveChangesAsync(Token);
        }
        var latest = await manager.SendMessageAsync(fixture.Account.AccountId, Draft("latest"), Token);
        Assert.Null(await manager.GetSendStatusAsync(evicted.MessageId, Token));
        Assert.Null(await manager.GetSendStatusAsync(pending.MessageId, Token));
        Assert.Null(await manager.GetOutgoingMessageAsync(evicted.MessageId, Token));
        Assert.Null(await manager.GetOutgoingMessageAsync(pending.MessageId, Token));

        var oldCanceled = Assert.Single(await snapshot.ReadNextAsync(1, Token));
        Assert.Equal(evicted.MessageId, oldCanceled.Status.MessageId);
        Assert.Equal(MessageSendState.Canceled, oldCanceled.Status.State);
        Assert.Equal("evicted", oldCanceled.Message.Body!.Text);
        var oldPending = Assert.Single(await snapshot.ReadNextAsync(1, Token));
        Assert.Equal(pending.MessageId, oldPending.Status.MessageId);
        Assert.Equal(MessageSendState.Queued, oldPending.Status.State);
        Assert.Equal("pending", oldPending.Message.Body!.Text);
        Assert.Empty(await snapshot.ReadNextAsync(1, Token));

        await using var refreshed = await manager.GetOutboxAsync(cancellationToken: Token);
        var current = await refreshed.ReadNextAsync(10, Token);
        Assert.Equal([first.MessageId, latest.MessageId], current.Select(item => item.Status.MessageId));
        Assert.Equal(["first", "latest"], current.Select(item => item.Message.Body!.Text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Invalid_stored_content_is_rejected_by_both_outbox_queries(string? json)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var row = Record(fixture, fixture.Account.AccountId, MessageSendState.Queued, "missing");
        row.PayloadJson = json;
        await using (var database = fixture.Database.Open())
        {
            database.MessageOutbox.Add(row);
            await database.SaveChangesAsync(Token);
        }
        await using var reader = await fixture.Client.MessageManager.GetOutboxAsync(cancellationToken: Token);
        if (json == "{")
        {
            await Assert.ThrowsAsync<JsonException>(() => reader.ReadNextAsync(10, Token));
            await Assert.ThrowsAsync<JsonException>(() => fixture.Client.MessageManager.GetOutgoingMessageAsync(row.MessageId, Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadNextAsync(10, Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.MessageManager.GetOutgoingMessageAsync(row.MessageId, Token));
        }
        Assert.Equal(MessageSendState.Queued, (await fixture.Client.MessageManager.GetSendStatusAsync(row.MessageId, Token))!.State);
    }

    [Fact]
    public async Task Single_query_validates_identifier_and_excludes_absent_and_internal_messages()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var row = Record(fixture, fixture.Account.AccountId, MessageSendState.Queued, "internal");
        row.IsDirect = false;
        row.PayloadJson = null;
        await using (var database = fixture.Database.Open())
        {
            database.MessageOutbox.Add(row);
            await database.SaveChangesAsync(Token);
        }
        var requests = fixture.Relay.Requests.Count;
        var manager = fixture.Client.MessageManager;
        Assert.Null(await manager.GetOutgoingMessageAsync(row.MessageId, Token));
        Assert.Null(await manager.GetOutgoingMessageAsync(Identifiers.CreateMessageId(), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.GetOutgoingMessageAsync("not-a-message-id", Token));
        Assert.Equal(MessageSendState.Queued, (await manager.GetSendStatusAsync(row.MessageId, Token))!.State);
        Assert.Equal(requests, fixture.Relay.Requests.Count);
    }

    static void AssertSame(OutgoingMessageInfo expected, OutgoingMessageInfo? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Message.ToJson(), actual.Message.ToJson());
        Assert.Equivalent(expected.Status, actual.Status);
    }

    static DirectMessageDraft Draft(string text) => new() { Body = new() { ContentType = "text/plain", Text = text } };

    static MessageOutboxRecord Record(TestClient fixture, string recipient, MessageSendState state, string text) => new()
    {
        MessageId = Identifiers.CreateMessageId(),
        Recipient = recipient,
        CreatedAt = Clock.UtcNow,
        IsDirect = true,
        State = state,
        RelayId = fixture.Relay.RelayId,
        RequestJson = "{}",
        PayloadJson = new DirectMessage { Body = new() { ContentType = "text/plain", Text = text } }.ToJson(),
        NextAttemptAt = Clock.UtcNow
    };
}
