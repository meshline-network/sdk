using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Collections.Concurrent;

namespace Meshline.Tests.Components.Messages;

public sealed class MessageSendingTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static DirectMessageDraft Draft(string text = "hello") => new()
    {
        Body = new()
        {
            ContentType = "text/plain",
            Text = text
        }
    };

    [Fact]
    public async Task Warm_route_is_reused_for_self_and_peer_device_queries_when_enqueueing()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var routes = fixture.Relay.Requests.Count(request => request.Method == "account.route.resolve");
        var devices = fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve");

        var queued = await fixture.Client.MessageManager.SendMessageAsync(peer.AccountId, Draft(), Token);

        Assert.Equal(MessageSendState.Queued, queued.State);
        Assert.Equal(routes, fixture.Relay.Requests.Count(request => request.Method == "account.route.resolve"));
        Assert.Equal(devices + 2, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
    }

    [Fact]
    public async Task Queued_message_can_be_canceled()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var manager = fixture.Client.MessageManager;
        var queued = await manager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);

        Assert.Equal(MessageSendState.Queued, queued.State);
        Assert.True(await manager.CancelMessageAsync(queued.MessageId, Token));
        Assert.Equal(MessageSendState.Canceled, (await manager.GetSendStatusAsync(queued.MessageId, Token))!.State);
    }

    [Fact]
    public async Task Inflight_state_recovers_after_restart_and_cannot_be_canceled()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var manager = fixture.Client.MessageManager;

        var pending = await manager.SendMessageAsync(fixture.Account.AccountId, Draft("inflight"), Token);
        await using (var db = fixture.Database.Open())
        {
            var row = await db.MessageOutbox.FindAsync([pending.MessageId], Token);
            row!.State = MessageSendState.Submitting;
            await db.SaveChangesAsync(Token);
        }

        await fixture.ReopenAsync();

        Assert.Equal(MessageSendState.SubmissionUnknown, (await fixture.Client.MessageManager.GetSendStatusAsync(pending.MessageId, Token))!.State);
        Assert.False(await fixture.Client.MessageManager.CancelMessageAsync(pending.MessageId, Token));
    }

    [Fact]
    public async Task Failed_send_retries_same_ciphertext_and_reports_completion()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var manager = fixture.Client.MessageManager;
        var queued = await manager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);
        var failed = AsyncTest.Signal();
        var completed = AsyncTest.Signal();
        var bodies = new ConcurrentQueue<string>();
        manager.SendStatusChanged += (_, change) =>
        {
            if (change.Status.MessageId != queued.MessageId)
                return;
            if (change.Status.State == MessageSendState.SubmissionUnknown)
                failed.TrySetResult();
            if (change.Status.State == MessageSendState.TargetAccepted)
                completed.TrySetResult();
        };
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "message.send" && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.MessageId == queued.MessageId)
            {
                bodies.Enqueue(request.Body!);
                if (bodies.Count == 1)
                    throw new HttpRequestException("Connection lost after submission");
            }

            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await manager.StartAsync(Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        // Stop drains the sender before advancing time, making the retry boundary deterministic.
        await manager.StopAsync(Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(15));
        await manager.StartAsync(Token);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await manager.StopAsync(Token);

        Assert.Equal(2, bodies.Count);
        Assert.Single(bodies.Distinct());
        var status = await manager.GetSendStatusAsync(queued.MessageId, Token);
        Assert.Equal(MessageSendState.TargetAccepted, status!.State);
        Assert.Equal(fixture.Relay.RelayId, status.AcceptedRelayId);
        Assert.NotNull(status.AcceptedAt);
        await fixture.ReopenAsync();
        var persisted = await fixture.Client.MessageManager.GetSendStatusAsync(queued.MessageId, Token);
        Assert.Equal(status.State, persisted!.State);
        Assert.Equal(status.AcceptedAt, persisted.AcceptedAt);
        Assert.Equal(status.AcceptedRelayId, persisted.AcceptedRelayId);
        Assert.False(await fixture.Client.MessageManager.CancelMessageAsync(queued.MessageId, Token));
    }
}
