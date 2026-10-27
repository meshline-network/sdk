using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;

namespace Meshline.Tests.Components.Contacts;

public sealed class DeviceStateNotificationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("restart")]
    [InlineData("reopen")]
    [InlineData("foreground")]
    public async Task Unchanged_state_does_not_broadcast_on_start_resume_or_ordinary_sends(string lifecycle)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await PublishAsync(fixture, fixture.Client.Device!, other.Certificate);
        using var peer = new AccountSigner();
        await PrepareContactAsync(fixture, peer);
        var revision = fixture.Client.DeviceState!.Revision;

        for (var i = 0; i < 3; i++)
        {
            if (lifecycle == "reopen") await fixture.ReopenAsync();
            // An unconfirmed grant makes completion of each maintenance pass
            // observable. Device notifications run before grant maintenance.
            await PrepareMaintenanceProbeAsync(fixture, peer.AccountId);
            if (lifecycle == "foreground") await fixture.Client.StartAsync(Token);
            else await fixture.Client.MessageManager.StartAsync(Token);
            await WaitForMaintenanceAsync(fixture, peer.AccountId);
            var sent = await fixture.Client.MessageManager.SendMessageAsync(peer.AccountId,
                new DirectMessageDraft { Body = new() { ContentType = "text/plain", Text = "ordinary message" } }, Token);
            await fixture.Client.MessageManager.WaitForSendStatusAsync(sent.MessageId, cancellationToken: Token)
                .WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (lifecycle == "foreground") await fixture.Client.StopAsync(Token);
            else await fixture.Client.MessageManager.StopAsync(Token);

            // Only the explicitly unconfirmed grant was created, with no device notification.
            Assert.Single(await PeerNotificationsAsync(fixture, peer.AccountId));
            Assert.Equal(revision, await CheckpointAsync(fixture, peer.AccountId));
        }
    }

    [Theory]
    [InlineData("running")]
    [InlineData("stopped")]
    [InlineData("reopened")]
    public async Task Real_device_certificate_and_gateway_changes_notify_once_per_revision(string lifecycle)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await PrepareContactAsync(fixture, peer);
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        if (lifecycle == "running") await fixture.Client.MessageManager.StartAsync(Token);

        for (var change = 0; change < 5; change++)
        {
            if (change == 0) await PublishAsync(fixture, fixture.Client.Device!, other.Certificate);
            else if (change == 1) await PublishAsync(fixture, fixture.Client.Device!);
            else
            {
                var renewed = await fixture.Client.DeviceManager.RenewDeviceAsync(new DeviceRenewalOptions
                {
                    Validity = TimeSpan.FromDays(20),
                    PushGateway = change == 2 ? FieldUpdate<string>.Unspecified
                        : change == 3 ? "https://push.example.com/callback" : FieldUpdate<string>.Delete
                }, Token);
                await PublishAsync(fixture, renewed);
            }
            var revision = fixture.Client.DeviceState!.Revision;
            if (lifecycle == "reopened") await fixture.ReopenAsync();
            if (lifecycle != "running") await fixture.Client.MessageManager.StartAsync(Token);
            await WaitForNotificationAsync(fixture, peer.AccountId, revision);
            if (lifecycle != "running") await fixture.Client.MessageManager.StopAsync(Token);

            Assert.Equal(change + 1, (await PeerNotificationsAsync(fixture, peer.AccountId)).Length);
        }
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, (await ReadNotificationRevisionsAsync(fixture, peer.AccountId)).Order());
    }

    [Fact]
    public async Task Startup_refresh_before_event_subscription_still_notifies_the_new_revision()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await PrepareContactAsync(fixture, peer);
        var previous = fixture.Client.DeviceState!;
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        var newer = previous with { Revision = previous.Revision + 1, Certificates = [fixture.Client.Device!, other.Certificate], AccountSignature = [] };
        fixture.Relay.Devices[fixture.Account.AccountId] = newer with { AccountSignature = [.. fixture.Account.Sign(newer.GetSigningInput(TestNetwork.Context))] };
        await using (var db = fixture.Database.Open())
            await db.DeviceStates.Where(value => value.AccountId == fixture.Account.AccountId).ExecuteDeleteAsync(Token);
        await fixture.ReopenAsync();
        Assert.Null(fixture.Client.DeviceState);

        await fixture.Client.MessageManager.StartAsync(Token);
        await WaitForNotificationAsync(fixture, peer.AccountId, newer.Revision);
        await fixture.Client.MessageManager.StopAsync(Token);

        Assert.Single(await PeerNotificationsAsync(fixture, peer.AccountId));
        Assert.Equal([newer.Revision], await ReadNotificationRevisionsAsync(fixture, peer.AccountId));
    }

    [Fact]
    public async Task Change_during_notification_preparation_uses_latest_revision_without_duplicate_fanout()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await PrepareContactAsync(fixture, peer);
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await PublishAsync(fixture, fixture.Client.Device!, other.Certificate);
        await using (var db = fixture.Database.Open())
            await db.DeviceStates.Where(value => value.AccountId == peer.AccountId).ExecuteDeleteAsync(Token);
        await fixture.ReopenAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (IsPeerQuery(request, peer.AccountId))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await PublishAsync(fixture, fixture.Client.Device!);
        }
        finally { release.TrySetResult(); }
        await WaitForNotificationAsync(fixture, peer.AccountId, fixture.Client.DeviceState!.Revision);
        await fixture.Client.MessageManager.StopAsync(Token);

        Assert.Single(await PeerNotificationsAsync(fixture, peer.AccountId));
        Assert.Equal([fixture.Client.DeviceState!.Revision], await ReadNotificationRevisionsAsync(fixture, peer.AccountId));
        // Drain another complete pass after the racing event and retain the checkpoint.
        await PrepareMaintenanceProbeAsync(fixture, peer.AccountId);
        await fixture.Client.MessageManager.StartAsync(Token);
        await WaitForMaintenanceAsync(fixture, peer.AccountId);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Single(await PeerNotificationsAsync(fixture, peer.AccountId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_checkpoint_rolls_back_notification_and_retries_only_unfinished_contacts(bool reopen)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var failedPeer = new AccountSigner();
        using var healthyPeer = new AccountSigner();
        await PrepareContactAsync(fixture, failedPeer);
        await PrepareContactAsync(fixture, healthyPeer);
        var baseline = fixture.Client.DeviceState!.Revision;
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await PublishAsync(fixture, fixture.Client.Device!, other.Certificate);
        var revision = fixture.Client.DeviceState!.Revision;
        await using (var db = fixture.Database.Open())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE BlockedNotification (AccountId TEXT);
                CREATE TRIGGER fail_checkpoint BEFORE UPDATE OF DeviceNotificationRevision ON Contacts
                WHEN EXISTS (SELECT 1 FROM BlockedNotification WHERE AccountId = NEW.AccountId)
                BEGIN SELECT RAISE(ABORT, 'checkpoint interrupted'); END;
                """, Token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BlockedNotification VALUES ({failedPeer.AccountId});", Token);
        }
        var failed = AsyncTest.Signal();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Resource == failedPeer.AccountId && error.Error is DbUpdateException) failed.TrySetResult();
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await WaitForNotificationAsync(fixture, healthyPeer.AccountId, revision);
        var healthy = Assert.Single(await PeerNotificationsAsync(fixture, healthyPeer.AccountId));
        Assert.Empty(await PeerNotificationsAsync(fixture, failedPeer.AccountId));
        Assert.Equal(baseline, await CheckpointAsync(fixture, failedPeer.AccountId));
        if (reopen) await fixture.Client.MessageManager.StopAsync(Token);
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_checkpoint;", Token);

        if (reopen)
        {
            await fixture.ReopenAsync();
            await fixture.Client.MessageManager.StartAsync(Token);
        }
        else fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(16));
        await WaitForNotificationAsync(fixture, failedPeer.AccountId, revision);
        await fixture.Client.MessageManager.StopAsync(Token);

        Assert.Single(await PeerNotificationsAsync(fixture, failedPeer.AccountId));
        Assert.Equal(healthy.RequestJson, Assert.Single(await PeerNotificationsAsync(fixture, healthyPeer.AccountId)).RequestJson);
    }

    [Fact]
    public async Task Stop_during_partial_fanout_resumes_remaining_contacts_after_reopening()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var first = new AccountSigner();
        using var second = new AccountSigner();
        await PrepareContactAsync(fixture, first);
        await PrepareContactAsync(fixture, second);
        var ids = new[] { first.AccountId, second.AccountId }.Order(StringComparer.Ordinal).ToArray();
        await using (var db = fixture.Database.Open())
            await db.DeviceStates.Where(value => value.AccountId == ids[1]).ExecuteDeleteAsync(Token);
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await PublishAsync(fixture, fixture.Client.Device!, other.Certificate);
        var revision = fixture.Client.DeviceState!.Revision;
        await fixture.ReopenAsync();
        var entered = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (IsPeerQuery(request, ids[1]))
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return fixture.Relay.Respond(request);
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await WaitForNotificationAsync(fixture, ids[0], revision);
        await fixture.Client.MessageManager.StopAsync(Token);
        var successful = Assert.Single(await PeerNotificationsAsync(fixture, ids[0]));
        Assert.Empty(await PeerNotificationsAsync(fixture, ids[1]));
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        // The interrupted recipient lookup retains its retry interval in the old
        // instance only; the new manager can resume immediately.
        await fixture.Client.MessageManager.StartAsync(Token);
        await WaitForNotificationAsync(fixture, ids[1], revision);
        await fixture.Client.MessageManager.StopAsync(Token);

        Assert.Single(await PeerNotificationsAsync(fixture, ids[1]));
        Assert.Equal(successful.RequestJson, Assert.Single(await PeerNotificationsAsync(fixture, ids[0])).RequestJson);
    }

    [Theory]
    [InlineData("rate_limited", HttpStatusCode.TooManyRequests, 45, false)]
    [InlineData("rate_limited", HttpStatusCode.TooManyRequests, 45, true)]
    [InlineData("route_stale", HttpStatusCode.BadRequest, null, false)]
    [InlineData("route_stale", HttpStatusCode.BadRequest, null, true)]
    [InlineData("target_not_local", HttpStatusCode.BadRequest, null, false)]
    [InlineData("target_not_local", HttpStatusCode.BadRequest, null, true)]
    [InlineData("unauthorized", HttpStatusCode.Unauthorized, null, false)]
    [InlineData("unauthorized", HttpStatusCode.Unauthorized, null, true)]
    public async Task Retryable_rejection_resumes_notification_without_recreating_successful_contacts(string code, HttpStatusCode statusCode, int? retryAfter, bool reopen)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var firstPeer = new AccountSigner();
        using var secondPeer = new AccountSigner();
        await PrepareContactAsync(fixture, firstPeer);
        await PrepareContactAsync(fixture, secondPeer);
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await PublishAsync(fixture, fixture.Client.Device!, other.Certificate);
        var revision = fixture.Client.DeviceState!.Revision;
        var rejected = AsyncTest.Signal();
        var requests = new ConcurrentQueue<string>();
        var delay = retryAfter ?? 15;
        var rejectRequests = true;
        var authentications = 0;
        string? healthyAccount = null;
        string? rejectedAccount = null;
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Error is RelayException rejection && rejection.Error.Code == code) rejected.TrySetResult();
        };
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "message.send")
            {
                var recipient = ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.To;
                if (recipient == firstPeer.AccountId || recipient == secondPeer.AccountId)
                {
                    // Accept the first recipient before applying relay-wide backoff to the second.
                    healthyAccount ??= recipient;
                    if (recipient != healthyAccount)
                    {
                        rejectedAccount = recipient;
                        requests.Enqueue(request.Body!);
                        if (Volatile.Read(ref rejectRequests))
                        {
                            if (code == "unauthorized") authentications = fixture.Relay.AuthenticationCount;
                            var response = OfflineRelay.Json(new RelayError
                            {
                                Code = code,
                                Message = "rejected",
                                Data = retryAfter is { } seconds ? ImmutableDictionary<string, JsonElement>.Empty.Add("retry_after", JsonSerializer.SerializeToElement(seconds)) : null
                            }, statusCode);
                            if (retryAfter is { } wait) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(wait));
                            return Task.FromResult(response);
                        }
                    }
                }
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.NotNull(healthyAccount);
        Assert.NotNull(rejectedAccount);
        await WaitForNotificationAsync(fixture, healthyAccount, revision);
        var original = Assert.Single(await PeerNotificationsAsync(fixture, rejectedAccount));
        var successful = Assert.Single(await PeerNotificationsAsync(fixture, healthyAccount));
        Assert.Equal(MessageSendState.Queued, original.State);
        Assert.Equal(Clock.UtcNow.AddSeconds(delay), original.NextAttemptAt);
        Assert.Equal(revision, await CheckpointAsync(fixture, rejectedAccount));
        if (reopen)
        {
            await fixture.Client.MessageManager.StopAsync(Token);
            await fixture.ReopenAsync();
        }
        Volatile.Write(ref rejectRequests, false);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(delay));
        if (reopen) await fixture.Client.MessageManager.StartAsync(Token);
        await WaitForNotificationAsync(fixture, rejectedAccount, revision);
        await fixture.Client.MessageManager.StopAsync(Token);
        var completed = Assert.Single(await PeerNotificationsAsync(fixture, rejectedAccount));
        Assert.Equal(original.MessageId, completed.MessageId);
        Assert.Equal(original.RequestJson, completed.RequestJson);
        Assert.Equal(successful.RequestJson, Assert.Single(await PeerNotificationsAsync(fixture, healthyAccount)).RequestJson);
        Assert.Equal(2, requests.Count);
        Assert.Single(requests.Distinct());
        if (code == "unauthorized") Assert.True(fixture.Relay.AuthenticationCount > authentications);
    }

    [Fact]
    public async Task Unknown_delivery_replays_exact_request_and_checkpoint_survives_outbox_retention()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await PrepareContactAsync(fixture, peer);
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await PublishAsync(fixture, fixture.Client.Device!, other.Certificate);
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "message.send" && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.To == peer.AccountId)
                throw new HttpRequestException("Response lost");
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () => (await PeerNotificationsAsync(fixture, peer.AccountId)) is [{ State: MessageSendState.SubmissionUnknown }]);
        await fixture.Client.MessageManager.StopAsync(Token);
        var pending = Assert.Single(await PeerNotificationsAsync(fixture, peer.AccountId));
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(16));
        fixture.Relay.Requests.Clear();
        await fixture.Client.MessageManager.StartAsync(Token);
        await WaitForNotificationAsync(fixture, peer.AccountId, fixture.Client.DeviceState!.Revision);
        await fixture.Client.MessageManager.StopAsync(Token);

        Assert.Equal(pending.RequestJson, Assert.Single(await PeerNotificationsAsync(fixture, peer.AccountId)).RequestJson);
        Assert.Equal(pending.RequestJson, Assert.Single(fixture.Relay.Requests, request => request.Method == "message.send"
            && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.To == peer.AccountId).Body);
        // Model terminal retention eviction, then prove a complete startup pass
        // does not need historical ciphertext to deduplicate the notification.
        await PrepareMaintenanceProbeAsync(fixture, peer.AccountId);
        await fixture.ReopenAsync();
        await fixture.Client.MessageManager.StartAsync(Token);
        await WaitForMaintenanceAsync(fixture, peer.AccountId);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Single(await PeerNotificationsAsync(fixture, peer.AccountId));
    }

    static async Task PrepareContactAsync(TestClient fixture, AccountSigner peer)
    {
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        await using var db = fixture.Database.Open();
        var contact = await db.Contacts.SingleAsync(value => value.AccountId == peer.AccountId, Token);
        // Seed a completed consent, leaving only device notifications as new peer work.
        contact.ConfirmedGrantToJson = contact.GrantToJson;
        await db.MessageOutbox.Where(value => value.Recipient == peer.AccountId).ExecuteDeleteAsync(Token);
        await db.SaveChangesAsync(Token);
    }

    static async Task PrepareMaintenanceProbeAsync(TestClient fixture, string accountId)
    {
        await using var db = fixture.Database.Open();
        (await db.Contacts.SingleAsync(value => value.AccountId == accountId, Token)).ConfirmedGrantToJson = null;
        await db.MessageOutbox.Where(value => value.Recipient == accountId && !value.IsDirect).ExecuteDeleteAsync(Token);
        await db.SaveChangesAsync(Token);
    }

    static Task WaitForMaintenanceAsync(TestClient fixture, string accountId) => AsyncTest.UntilAsync(async () =>
    {
        await using var db = fixture.Database.Open();
        var contact = await db.Contacts.SingleAsync(value => value.AccountId == accountId, Token);
        return contact.ConfirmedGrantToJson == contact.GrantToJson;
    });

    static Task WaitForNotificationAsync(TestClient fixture, string accountId, long revision) => AsyncTest.UntilAsync(async () =>
        await CheckpointAsync(fixture, accountId) == revision
        && (await PeerNotificationsAsync(fixture, accountId)) is { Length: > 0 } sends
        && sends.All(value => value.State == MessageSendState.TargetAccepted));

    static async Task<long> CheckpointAsync(TestClient fixture, string accountId)
    {
        await using var db = fixture.Database.Open();
        return (await db.Contacts.SingleAsync(value => value.AccountId == accountId, Token)).DeviceNotificationRevision;
    }

    static async Task<MessageOutboxRecord[]> PeerNotificationsAsync(TestClient fixture, string accountId)
    {
        await using var db = fixture.Database.Open();
        return await db.MessageOutbox.AsNoTracking().Where(value => value.Recipient == accountId && !value.IsDirect)
            .OrderBy(value => value.MessageId).ToArrayAsync(Token);
    }

    static async Task<long[]> ReadNotificationRevisionsAsync(TestClient fixture, string accountId)
    {
        var requests = (await PeerNotificationsAsync(fixture, accountId))
            .Select(value => ProtocolModel.FromJson<MessageSendRequest>(value.RequestJson)!).ToArray();
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var previousHandler = fixture.Relay.Handler;
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "message.timeline.sync"
            ? OfflineRelay.Json(new MessageTimelinePage
            {
                Items = [.. requests.Select((request, index) => new MessageTimelineEntry
                {
                    Sequence = index,
                    AcceptedAt = request.Envelope.CreatedAt,
                    Envelope = request.Envelope,
                    KeyBox = request.SenderBoxes!.Value.Single(value => value.DeviceId == deviceId)
                })],
                Certificates = [fixture.Client.Device!],
                HasMore = false
            }) : fixture.Relay.Respond(request));
        try
        {
            // Exercise the real sender-copy decryption and validate the wire payload.
            await fixture.Client.MessageManager.SynchronizeAsync(fixture.Relay.RelayId, Token);
            var messages = await fixture.Client.MessageManager.ReadTimelineAsync(0, 100, Token);
            return messages.Select(value => Assert.IsType<DeviceStateChanged>(value.Payload).Revision).ToArray();
        }
        finally { fixture.Relay.Handler = previousHandler; }
    }

    static Task<DeviceStatePublishResult> PublishAsync(TestClient fixture, params DeviceCertificate[] certificates) =>
        fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, certificates, cancellationToken: Token);

    static bool IsPeerQuery(ObservedRequest request, string accountId) => request.Method == "device.state.resolve"
        && request.Body?.Contains(accountId, StringComparison.Ordinal) == true;
}
