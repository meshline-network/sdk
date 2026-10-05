using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Components.Messages;

public sealed class SendStatusWaitingTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static DirectMessageDraft Draft => new() { Body = new() { ContentType = "text/plain", Text = "wait for delivery" } };

    [Theory]
    [InlineData(MessageSendState.Queued, MessageSendState.Queued)]
    [InlineData(MessageSendState.SubmissionUnknown, MessageSendState.Queued)]
    [InlineData(MessageSendState.RelayAccepted, MessageSendState.RelayAccepted)]
    [InlineData(MessageSendState.TargetAccepted, MessageSendState.RelayAccepted)]
    [InlineData(MessageSendState.TargetAccepted, MessageSendState.TargetAccepted)]
    [InlineData(MessageSendState.Failed, MessageSendState.RelayAccepted)]
    [InlineData(MessageSendState.Failed, MessageSendState.TargetAccepted)]
    [InlineData(MessageSendState.Canceled, MessageSendState.TargetAccepted)]
    public async Task Retained_status_returns_actual_state_without_network_requests(MessageSendState current, MessageSendState target)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var queued = await manager.SendMessageAsync(fixture.Account.AccountId, Draft, Token);
        await using (var db = fixture.Database.Open())
        {
            var row = (await db.MessageOutbox.FindAsync([queued.MessageId], Token))!;
            row.State = current;
            row.AcceptedAt = current is MessageSendState.RelayAccepted or MessageSendState.TargetAccepted ? Clock.UtcNow : null;
            row.ErrorMessage = current == MessageSendState.Failed ? "delivery rejected" : null;
            await db.SaveChangesAsync(Token);
        }
        await fixture.ReopenAsync();
        var requests = fixture.Relay.Requests.Count;
        var status = await fixture.Client.MessageManager.WaitForSendStatusAsync(queued.MessageId, target, Token);
        Assert.Equal(current, status!.State);
        if (current == MessageSendState.Failed) Assert.Equal("delivery rejected", status.ErrorMessage);
        Assert.Equal(requests, fixture.Relay.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waits_for_each_acceptance_milestone_and_survives_stop_start(bool failDelivery)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var queued = await manager.SendMessageAsync(fixture.Account.AccountId, Draft, Token);
        var acceptedAt = Clock.UtcNow.ToUnixTimeSeconds();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method is "message.send" or "message.delivery.status"
            ? OfflineRelay.Json(new MessageDeliveryStatus
            {
                Status = request.Method == "message.send" ? MessageDeliveryState.Delivering : failDelivery ? MessageDeliveryState.Failed : MessageDeliveryState.TargetAccepted,
                AcceptedAt = acceptedAt,
                Error = request.Method == "message.delivery.status" && failDelivery ? new() { Code = "message_expired", Message = "delivery expired" } : null
            })
            : fixture.Relay.Respond(request));
        var relay = manager.WaitForSendStatusAsync(queued.MessageId, MessageSendState.RelayAccepted, Token);
        var target = manager.WaitForSendStatusAsync(queued.MessageId, cancellationToken: Token);
        Assert.False(target.IsCompleted);
        await manager.StartAsync(Token);
        Assert.Equal(MessageSendState.RelayAccepted, (await relay.WaitAsync(TimeSpan.FromSeconds(10), Token))!.State);
        await manager.StopAsync(Token);
        Assert.False(target.IsCompleted);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(15));
        await manager.StartAsync(Token);
        var completed = await target.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(failDelivery ? MessageSendState.Failed : MessageSendState.TargetAccepted, completed!.State);
        if (failDelivery) Assert.Equal("delivery expired", completed.ErrorMessage);
        Assert.Equal(fixture.Relay.RelayId, completed.AcceptedRelayId);
    }

    [Fact]
    public async Task Canceling_one_wait_does_not_cancel_the_send_or_other_waiters()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var queued = await manager.SendMessageAsync(fixture.Account.AccountId, Draft, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var canceled = manager.WaitForSendStatusAsync(queued.MessageId, cancellationToken: cancellation.Token);
        var other = manager.WaitForSendStatusAsync(queued.MessageId, cancellationToken: Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(MessageSendState.Queued, (await manager.GetSendStatusAsync(queued.MessageId, Token))!.State);
        Assert.False(other.IsCompleted);
        await manager.StartAsync(Token);
        Assert.Equal(MessageSendState.TargetAccepted, (await other.WaitAsync(TimeSpan.FromSeconds(10), Token))!.State);
    }

    [Fact]
    public async Task Committed_cancellation_completes_wait_even_if_public_observer_throws()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var queued = await manager.SendMessageAsync(fixture.Account.AccountId, Draft, Token);
        manager.SendStatusChanged += (_, _) => throw new InvalidOperationException("observer failure");
        var waiting = manager.WaitForSendStatusAsync(queued.MessageId, cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CancelMessageAsync(queued.MessageId, Token));
        Assert.Equal(MessageSendState.Canceled, (await waiting.WaitAsync(TimeSpan.FromSeconds(10), Token))!.State);
    }

    [Fact]
    public async Task Disposal_cancels_and_drains_wait_without_canceling_the_send()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.MessageManager;
        var queued = await manager.SendMessageAsync(fixture.Account.AccountId, Draft, Token);
        var waiting = manager.WaitForSendStatusAsync(queued.MessageId, cancellationToken: Token);
        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await fixture.ReopenAsync();
        Assert.Equal(MessageSendState.Queued, (await fixture.Client.MessageManager.GetSendStatusAsync(queued.MessageId, Token))!.State);
    }

    [Fact]
    public async Task Missing_records_return_null_and_invalid_targets_are_rejected()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        var manager = fixture.Client.MessageManager;
        var id = Identifiers.CreateMessageId();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WaitForSendStatusAsync(id, cancellationToken: Token));
        await fixture.InitializeAsync();
        Assert.Null(await manager.WaitForSendStatusAsync(id, cancellationToken: Token));
        foreach (var target in new[] { MessageSendState.None, MessageSendState.All, MessageSendState.Submitting, MessageSendState.SubmissionUnknown,
            MessageSendState.Failed, MessageSendState.Canceled, MessageSendState.RelayAccepted | MessageSendState.TargetAccepted })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.WaitForSendStatusAsync(id, target, Token));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.WaitForSendStatusAsync(id, cancellationToken: canceled.Token));
    }
}
