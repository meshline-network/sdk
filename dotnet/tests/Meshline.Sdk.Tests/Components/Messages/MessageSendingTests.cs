using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Meshline.Tests.Components.Messages;

public sealed class MessageSendingTests
{
    const long MaximumProtocolInteger = 9_007_199_254_740_991;
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
        Assert.Equal(devices, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
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

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(45L)]
    [InlineData(MaximumProtocolInteger)]
    public async Task Rate_limit_preserves_retry_deadline_and_exact_request_across_reopen(long? retryAfter)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var start = Clock.UtcNow;
        var queued = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);
        var limited = AsyncTest.Signal();
        var bodies = new ConcurrentQueue<string>();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Resource == queued.MessageId) limited.TrySetResult();
        };
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "message.send" && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.MessageId == queued.MessageId)
            {
                bodies.Enqueue(request.Body!);
                if (bodies.Count == 1) return Task.FromResult(RateLimited(retryAfter));
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await limited.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        await fixture.ReopenAsync();
        var deadline = retryAfter == MaximumProtocolInteger ? DateTimeOffset.MaxValue : start.AddSeconds(Math.Max(15, retryAfter ?? 0));
        await using (var db = fixture.Database.Open())
        {
            var row = (await db.MessageOutbox.FindAsync([queued.MessageId], Token))!;
            Assert.Equal(MessageSendState.Queued, row.State);
            Assert.Equal(deadline.ToUnixTimeMilliseconds(), row.NextAttemptAt.ToUnixTimeMilliseconds());
            Assert.Equal(Assert.Single(bodies), row.RequestJson);
            Assert.Null(row.AcceptedAt);
            Assert.Equal("limited", row.ErrorMessage);
        }
        if (retryAfter == MaximumProtocolInteger) return;

        fixture.Relay.Clock.Advance(deadline - Clock.UtcNow - TimeSpan.FromSeconds(1));
        await fixture.Client.MessageManager.StartAsync(Token);
        var probe = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft("before retry deadline"), Token);
        await fixture.Client.MessageManager.WaitForSendStatusAsync(probe.MessageId, cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Single(bodies);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Client.MessageManager.StartAsync(Token);
        var completed = await fixture.Client.MessageManager.WaitForSendStatusAsync(queued.MessageId, cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(MessageSendState.TargetAccepted, completed!.State);
        Assert.Equal(2, bodies.Count);
        Assert.Single(bodies.Distinct());
    }

    [Theory]
    [InlineData(MessageSendState.SubmissionUnknown, "rate_limited", HttpStatusCode.TooManyRequests, 45)]
    [InlineData(MessageSendState.RelayAccepted, "rate_limited", HttpStatusCode.TooManyRequests, 45)]
    [InlineData(MessageSendState.SubmissionUnknown, "route_stale", HttpStatusCode.BadRequest, null)]
    [InlineData(MessageSendState.RelayAccepted, "route_stale", HttpStatusCode.BadRequest, null)]
    [InlineData(MessageSendState.SubmissionUnknown, "target_not_local", HttpStatusCode.BadRequest, null)]
    [InlineData(MessageSendState.RelayAccepted, "target_not_local", HttpStatusCode.BadRequest, null)]
    [InlineData(MessageSendState.SubmissionUnknown, "unauthorized", HttpStatusCode.Unauthorized, null)]
    [InlineData(MessageSendState.RelayAccepted, "unauthorized", HttpStatusCode.Unauthorized, null)]
    public async Task Retryable_rejection_preserves_existing_acceptance_evidence(MessageSendState state, string code, HttpStatusCode statusCode, int? retryAfter)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var start = Clock.UtcNow;
        var delay = retryAfter ?? 15;
        var queued = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);
        var acceptedAt = state == MessageSendState.RelayAccepted ? start : (DateTimeOffset?)null;
        await using (var db = fixture.Database.Open())
        {
            var row = (await db.MessageOutbox.FindAsync([queued.MessageId], Token))!;
            row.State = state;
            row.AcceptedAt = acceptedAt;
            await db.SaveChangesAsync(Token);
        }
        var rejected = AsyncTest.Signal();
        var requests = new ConcurrentQueue<ObservedRequest>();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Resource == queued.MessageId) rejected.TrySetResult();
        };
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "message.send" && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.MessageId == queued.MessageId
                || request.Method == "message.delivery.status" && RequestQuery.Parse(request)["message_id"] == queued.MessageId)
            {
                requests.Enqueue(request);
                if (requests.Count == 1)
                {
                    var response = OfflineRelay.Json(new RelayError
                    {
                        Code = code,
                        Message = "rejected",
                        Data = retryAfter is { } seconds ? ImmutableDictionary<string, JsonElement>.Empty.Add("retry_after", JsonSerializer.SerializeToElement(seconds)) : null
                    }, statusCode);
                    if (retryAfter is { } wait) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(wait));
                    return Task.FromResult(response);
                }
                return Task.FromResult(OfflineRelay.Json(new MessageDeliveryStatus
                {
                    Status = MessageDeliveryState.TargetAccepted,
                    AcceptedAt = start.ToUnixTimeSeconds()
                }));
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        var discoveries = fixture.Relay.Requests.Count(request => request.Method == "account.route.resolve");
        await fixture.Client.MessageManager.StartAsync(Token);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(discoveries, fixture.Relay.Requests.Count(request => request.Method == "account.route.resolve"));
        await fixture.ReopenAsync();
        await using (var db = fixture.Database.Open())
        {
            var row = (await db.MessageOutbox.FindAsync([queued.MessageId], Token))!;
            Assert.Equal(state, row.State);
            Assert.Equal(acceptedAt, row.AcceptedAt);
            Assert.Equal(fixture.Relay.RelayId, row.RelayId);
            Assert.Equal(start.AddSeconds(delay), row.NextAttemptAt);
        }
        Assert.False(await fixture.Client.MessageManager.CancelMessageAsync(queued.MessageId, Token));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(delay));
        await fixture.Client.MessageManager.StartAsync(Token);
        var completed = await fixture.Client.MessageManager.WaitForSendStatusAsync(queued.MessageId, cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(MessageSendState.TargetAccepted, completed!.State);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal(state == MessageSendState.RelayAccepted ? "message.delivery.status" : "message.send", request.Method));
        Assert.Single(requests.Select(value => value.Body).Distinct());
    }

    [Theory]
    [InlineData("route_stale", "none")]
    [InlineData("target_not_local", "none")]
    [InlineData("route_stale", "failure")]
    [InlineData("target_not_local", "failure")]
    [InlineData("route_stale", "cancellation")]
    [InlineData("target_not_local", "cancellation")]
    public async Task Rejected_route_retries_exact_request_at_discovered_home_after_interruption_and_reopen(string code, string interruption)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var target = new OfflineRelay("new-home.test");
        fixture.Relay.LinkTo(target);
        var route = fixture.Client.Route! with { RelayId = target.RelayId, Revision = fixture.Client.Route.Revision + 1 };
        route = target.SignRoute(route with { AccountSignature = [.. fixture.Account.Sign(route.GetAccountSigningInput(TestNetwork.Context))] });
        target.Routes[fixture.Account.AccountId] = fixture.Relay.Routes[fixture.Account.AccountId] = route;
        target.Devices[fixture.Account.AccountId] = fixture.Client.DeviceState!;
        var queued = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);
        var discovering = AsyncTest.Signal();
        var rejected = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = new ConcurrentQueue<string>();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Resource == queued.MessageId) rejected.TrySetResult(error.Error);
        };
        var interrupt = true;
        var refreshes = 0;
        async Task<HttpResponseMessage> Respond(OfflineRelay relay, ObservedRequest request, CancellationToken token)
        {
            if (request.Method == "account.route.resolve")
            {
                Interlocked.Increment(ref refreshes);
                await using var db = fixture.Database.Open();
                Assert.Equal(MessageSendState.Queued, (await db.MessageOutbox.FindAsync([queued.MessageId], token))!.State);
                discovering.TrySetResult();
                if (Volatile.Read(ref interrupt))
                {
                    if (interruption == "failure") throw new HttpRequestException("Discovery unavailable");
                    if (interruption == "cancellation") await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }
            if (request.Method == "message.send" && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.MessageId == queued.MessageId)
            {
                bodies.Enqueue(request.Body!);
                if (relay == fixture.Relay) return OfflineRelay.Error(code);
            }
            return relay.Respond(request);
        }
        fixture.Relay.Handler = (request, token) => Respond(fixture.Relay, request, token);
        target.Handler = (request, token) => Respond(target, request, token);
        await fixture.Client.MessageManager.StartAsync(Token);
        if (interruption == "cancellation") await discovering.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        else
        {
            var failure = await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (interruption == "failure")
            {
                var errors = Assert.IsType<AggregateException>(failure).InnerExceptions;
                Assert.Equal(code, Assert.IsType<RelayException>(errors[0]).Error.Code);
                Assert.IsType<HttpRequestException>(errors[1]);
            }
            else Assert.Equal(code, Assert.IsType<RelayException>(failure).Error.Code);
        }
        await fixture.Client.MessageManager.StopAsync(Token);
        await fixture.ReopenAsync();
        await using (var db = fixture.Database.Open())
        {
            var pending = (await db.MessageOutbox.FindAsync([queued.MessageId], Token))!;
            Assert.Equal(MessageSendState.Queued, pending.State);
            Assert.Equal(Assert.Single(bodies), pending.RequestJson);
            Assert.Null(pending.AcceptedAt);
        }
        Volatile.Write(ref interrupt, false);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(15));
        await fixture.Client.MessageManager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Client.Route!.RelayId == target.RelayId);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(15));
        var completed = await fixture.Client.MessageManager.WaitForSendStatusAsync(queued.MessageId, cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(MessageSendState.TargetAccepted, completed!.State);
        Assert.Equal(target.RelayId, completed.AcceptedRelayId);
        Assert.True(refreshes > 0);
        Assert.Single(bodies.Distinct());
        Assert.Single(target.Requests, request => request.Method == "message.send" && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.MessageId == queued.MessageId);
    }

    [Fact]
    public async Task Nonretryable_rejection_remains_failed_after_reopen()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var queued = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);
        var failed = AsyncTest.Signal();
        var attempts = 0;
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Resource == queued.MessageId) failed.TrySetResult();
        };
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "message.send" && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.MessageId == queued.MessageId)
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(OfflineRelay.Error("invalid_signature"));
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        await fixture.ReopenAsync();
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.Client.MessageManager.StartAsync(Token);
        var probe = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft("after permanent failure"), Token);
        await fixture.Client.MessageManager.WaitForSendStatusAsync(probe.MessageId, cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(MessageSendState.Failed, (await fixture.Client.MessageManager.GetSendStatusAsync(queued.MessageId, Token))!.State);
        Assert.Equal(1, attempts);
    }

    static HttpResponseMessage RateLimited(long? retryAfter)
    {
        var response = OfflineRelay.Json(new RelayError
        {
            Code = "rate_limited",
            Message = "limited",
            Data = retryAfter is { } delay ? ImmutableDictionary<string, JsonElement>.Empty.Add("retry_after", JsonSerializer.SerializeToElement(delay)) : null
        }, HttpStatusCode.TooManyRequests);
        if (retryAfter is { } value) response.Headers.TryAddWithoutValidation("Retry-After", value.ToString(CultureInfo.InvariantCulture));
        return response;
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
