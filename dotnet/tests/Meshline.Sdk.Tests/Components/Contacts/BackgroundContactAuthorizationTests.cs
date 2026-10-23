using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;

namespace Meshline.Tests.Components.Contacts;

public sealed class BackgroundContactAuthorizationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(MessageDeliveryState.Delivering)]
    [InlineData(MessageDeliveryState.Failed)]
    [InlineData(MessageDeliveryState.TargetAccepted)]
    public async Task Only_target_acceptance_confirms_supplemented_authorization(MessageDeliveryState delivery)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        await PrepareSynchronizedContactAsync(fixture, peer);
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Handler = (request, _) => Task.FromResult(IsPeerSend(request, peer.AccountId)
            ? OfflineRelay.Json(new MessageDeliveryStatus
            {
                Status = delivery,
                AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds(),
                Error = delivery == MessageDeliveryState.Failed ? new RelayError { Code = "forbidden", Message = "delivery rejected" } : null
            }) : fixture.Relay.Respond(request));
        var expected = delivery switch
        {
            MessageDeliveryState.Delivering => MessageSendState.RelayAccepted,
            MessageDeliveryState.Failed => MessageSendState.Failed,
            _ => MessageSendState.TargetAccepted
        };

        await fixture.Client.MessageManager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () =>
        {
            await using var db = fixture.Database.Open();
            var sends = await db.MessageOutbox.Where(value => value.Recipient == peer.AccountId && value.State == expected).ToListAsync(Token);
            return sends.Count(value => ProtocolModel.FromJson<MessageSendRequest>(value.RequestJson)!.Envelope.FromDeviceId == deviceId) == 2;
        });
        await fixture.Client.MessageManager.StopAsync(Token);

        Assert.Equal(delivery == MessageDeliveryState.TargetAccepted, await HasLocalSignatureAsync(fixture, confirmed: true));
        await fixture.ReopenAsync();
        Assert.Equal(delivery == MessageDeliveryState.TargetAccepted, await HasLocalSignatureAsync(fixture, confirmed: true));
    }

    [Fact]
    public async Task Restart_after_setup_discovers_updates_and_preserves_concurrent_contact_edits()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        await PrepareSynchronizedContactAsync(fixture, peer);
        // Setup finished before any grant update was prepared. No in-memory wake-up survives.
        await fixture.ReopenAsync();
        var manager = fixture.Client.MessageManager;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (IsPeerSend(request, peer.AccountId))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var aliasChanged = false;
        manager.ContactChanged += (sender, _) => { Assert.Same(manager, sender); aliasChanged = true; };
        await manager.StartAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await AsyncTest.UntilAsync(() => HasLocalSignatureAsync(fixture, confirmed: false));
            await manager.SetAliasAsync(peer.AccountId, "Retained alias", Token);
            Assert.True(aliasChanged);
            Assert.False(await HasLocalSignatureAsync(fixture, confirmed: true));
            await using var db = fixture.Database.Open();
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
        }
        finally { release.TrySetResult(); }

        await AsyncTest.UntilAsync(() => HasLocalSignatureAsync(fixture, confirmed: true));
        await manager.StopAsync(Token);
        Assert.Equal("Retained alias", (await manager.GetContactAsync(peer.AccountId, Token))!.Alias);
    }

    [Fact]
    public async Task Unknown_delivery_reuses_requests_and_completes_public_waiters_after_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        await PrepareSynchronizedContactAsync(fixture, peer);
        var pending = await InterruptDeliveryAsync(fixture, peer.AccountId);
        await fixture.ReopenAsync();
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(16));
        fixture.Relay.Requests.Clear();
        var waits = pending.Select(value => fixture.Client.MessageManager.WaitForSendStatusAsync(value.MessageId, cancellationToken: Token)).ToArray();

        await fixture.Client.MessageManager.StartAsync(Token);

        foreach (var wait in waits)
            Assert.Equal(MessageSendState.TargetAccepted, (await wait.WaitAsync(TimeSpan.FromSeconds(5), Token))!.State);
        await AsyncTest.UntilAsync(() => HasLocalSignatureAsync(fixture, confirmed: true));
        await fixture.Client.MessageManager.StopAsync(Token);
        foreach (var original in pending)
            Assert.Equal(original.RequestJson, Assert.Single(fixture.Relay.Requests, request => IsMessageSend(request, original.MessageId)).Body);
        await using var db = fixture.Database.Open();
        Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "device.state.publish" or "account.route.publish");
    }

    [Fact]
    public async Task Failed_confirmation_commit_rolls_back_status_and_replays_the_original_request_after_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        await PrepareSynchronizedContactAsync(fixture, peer);
        var pending = await InterruptDeliveryAsync(fixture, peer.AccountId);
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_confirmation AFTER UPDATE OF ConfirmedGrantToJson ON Contacts BEGIN SELECT RAISE(ABORT, 'confirmation interrupted'); END;", Token);
        var failed = AsyncTest.Signal();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Error is DbUpdateException) failed.TrySetResult();
        };
        var waits = pending.ToDictionary(value => value.MessageId, value => fixture.Client.MessageManager.WaitForSendStatusAsync(value.MessageId, cancellationToken: Token));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(16));
        await fixture.Client.MessageManager.StartAsync(Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await fixture.Client.MessageManager.StopAsync(Token);

        MessageOutboxRecord rolledBack;
        await using (var db = fixture.Database.Open())
        {
            rolledBack = Assert.Single(await db.MessageOutbox.Where(value => value.Recipient == peer.AccountId && value.State == MessageSendState.SubmissionUnknown).ToListAsync(Token));
            Assert.Null(rolledBack.AcceptedAt);
            Assert.Equal(pending.Single(value => value.MessageId == rolledBack.MessageId).RequestJson, rolledBack.RequestJson);
            Assert.False(waits[rolledBack.MessageId].IsCompleted);
            Assert.False(await HasLocalSignatureAsync(fixture, confirmed: true));
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_confirmation;", Token);
        }
        await fixture.ReopenAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waits[rolledBack.MessageId]);
        fixture.Relay.Requests.Clear();

        await fixture.Client.MessageManager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => HasLocalSignatureAsync(fixture, confirmed: true));
        await fixture.Client.MessageManager.StopAsync(Token);

        Assert.Equal(rolledBack.RequestJson, Assert.Single(fixture.Relay.Requests, request => IsMessageSend(request, rolledBack.MessageId)).Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_drains_background_updates_and_retains_their_requests(bool disposeClient)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        await PrepareSynchronizedContactAsync(fixture, peer);
        var pending = await InterruptDeliveryAsync(fixture, peer.AccountId);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(16));
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        string? interruptedId = null;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (pending.Any(value => IsMessageSend(request, value.MessageId)))
            {
                interruptedId = ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.MessageId;
                entered.TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        Task disposal;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            disposal = disposeClient ? fixture.Client.DisposeAsync().AsTask() : fixture.Client.MessageManager.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(disposal.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await using (var db = fixture.Database.Open())
        {
            Assert.Equal(MessageSendState.SubmissionUnknown, (await db.MessageOutbox.SingleAsync(value => value.MessageId == interruptedId, Token)).State);
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
        }
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(16));
        fixture.Relay.Requests.Clear();

        await fixture.Client.MessageManager.StartAsync(Token);
        // Either pending update can confirm the grant. Wait for the request this
        // test actually interrupted before stopping the sender and checking replay.
        var restored = await fixture.Client.MessageManager.WaitForSendStatusAsync(interruptedId!, cancellationToken: Token)
            .WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(MessageSendState.TargetAccepted, restored!.State);
        await AsyncTest.UntilAsync(() => HasLocalSignatureAsync(fixture, confirmed: true));
        await fixture.Client.MessageManager.StopAsync(Token);

        var original = pending.Single(value => value.MessageId == interruptedId);
        Assert.Equal(original.RequestJson, Assert.Single(fixture.Relay.Requests, request => IsMessageSend(request, interruptedId!)).Body);
    }

    [Fact]
    public async Task Failed_preparation_commit_retries_without_another_device_event_or_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        await PrepareSynchronizedContactAsync(fixture, peer);
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_supplement BEFORE UPDATE OF GrantToJson ON Contacts BEGIN SELECT RAISE(ABORT, 'supplement interrupted'); END;", Token);
        var failed = AsyncTest.Signal();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Resource == peer.AccountId && error.Error is DbUpdateException) failed.TrySetResult();
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(await HasLocalSignatureAsync(fixture, confirmed: false));
        await using (var db = fixture.Database.Open())
        {
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_supplement;", Token);
        }

        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(16));

        await AsyncTest.UntilAsync(() => HasLocalSignatureAsync(fixture, confirmed: true));
        await fixture.Client.MessageManager.StopAsync(Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_supplemented_but_unqueued_grants_resume_when_incoming_authorization_is_available(bool missingIncoming)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var peer = new AccountSigner();
        await PrepareSynchronizedContactAsync(fixture, peer);
        string? incoming;
        await using (var db = fixture.Database.Open())
        {
            var contact = await db.Contacts.SingleAsync(Token);
            incoming = contact.GrantFromJson;
            if (missingIncoming) contact.GrantFromJson = null;
            var grant = ProtocolModel.FromJson<ContactGrant>(contact.GrantToJson!)!;
            var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
            var signature = await fixture.Client.DeviceManager.SignAsync(grant.GetSigningInput(TestNetwork.Context), Token);
            contact.GrantToJson = (grant with { Signatures = grant.Signatures.Add(deviceId, [.. signature]) }).ToJson();
            await db.SaveChangesAsync(Token);
        }
        await fixture.ReopenAsync();
        var failed = AsyncTest.Signal();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Resource == peer.AccountId && error.Error is InvalidOperationException) failed.TrySetResult();
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        if (missingIncoming)
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(await HasLocalSignatureAsync(fixture, confirmed: true));
            await using var db = fixture.Database.Open();
            (await db.Contacts.SingleAsync(Token)).GrantFromJson = incoming;
            await db.SaveChangesAsync(Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(16));
        }

        await AsyncTest.UntilAsync(() => HasLocalSignatureAsync(fixture, confirmed: true));
        await fixture.Client.MessageManager.StopAsync(Token);
    }

    [Fact]
    public async Task Unavailable_contact_does_not_block_healthy_grants_or_repeat_successful_notifications()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var unavailable = new AccountSigner();
        using var healthy = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, unavailable, Token);
        await ContactSetup.AcceptPeerAsync(fixture, healthy, Token);
        var signer = new DeviceSigner(unavailable, fixture.Relay.Clock);
        var certificate = signer.Certificate with
        {
            ExpiresAt = Clock.UtcNow.AddSeconds(1).ToUnixTimeSeconds(),
            DeviceSignature = [],
            AccountSignature = []
        };
        certificate = certificate with { DeviceSignature = [.. await signer.SignAsync(certificate.GetDeviceSigningInput(TestNetwork.Context), Token)] };
        certificate = certificate with { AccountSignature = [.. unavailable.Sign(certificate.GetAccountSigningInput(TestNetwork.Context))] };
        var state = fixture.Relay.Devices[unavailable.AccountId] with { Revision = 1, Certificates = [certificate], AccountSignature = [] };
        fixture.Relay.Devices[unavailable.AccountId] = state with { AccountSignature = [.. unavailable.Sign(state.GetSigningInput(TestNetwork.Context))] };
        ContactGrant incoming;
        await using (var db = fixture.Database.Open())
            incoming = ProtocolModel.FromJson<ContactGrant>((await db.Contacts.SingleAsync(value => value.AccountId == unavailable.AccountId, Token)).GrantFromJson!)!;
        await fixture.Client.DeviceManager.RefreshDeviceStateAsync(incoming, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        await ImportRemoteGrantsAsync(fixture);
        var currentId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var failed = AsyncTest.Signal();
        var retried = AsyncTest.Signal();
        var queries = 0;
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "device.state.resolve" && request.Body?.Contains(unavailable.AccountId, StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref queries);
                throw new HttpRequestException("Contact temporarily unavailable.");
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Resource == unavailable.AccountId && error.Error is HttpRequestException)
            {
                failed.TrySetResult();
                if (Volatile.Read(ref queries) >= 2) retried.TrySetResult();
            }
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await AsyncTest.UntilAsync(() => ConfirmedAsync(healthy.AccountId));
        var successful = await CurrentSendsAsync(healthy.AccountId);
        Assert.Equal(2, successful.Length);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(61));
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // The failed contact later becomes available; its notification and grant
        // must still run, without enqueuing another notification for the healthy one.
        state = state with { Revision = 2, Certificates = [signer.Certificate], AccountSignature = [] };
        fixture.Relay.Devices[unavailable.AccountId] = state with { AccountSignature = [.. unavailable.Sign(state.GetSigningInput(TestNetwork.Context))] };
        fixture.Relay.Handler = null;
        await AsyncTest.UntilAsync(() => Task.FromResult(fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(15))));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(61));
        await AsyncTest.UntilAsync(() => ConfirmedAsync(unavailable.AccountId));
        // A grant may succeed in the pass whose earlier notification failed.
        // Drive the next maintenance pass and wait for both durable effects.
        if ((await CurrentSendsAsync(unavailable.AccountId)).Length < 2)
        {
            await AsyncTest.UntilAsync(() => Task.FromResult(fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(15))));
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(61));
        }
        await AsyncTest.UntilAsync(async () => (await CurrentSendsAsync(unavailable.AccountId)).Length == 2);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(successful, await CurrentSendsAsync(healthy.AccountId));
        Assert.Equal(2, (await CurrentSendsAsync(unavailable.AccountId)).Length);

        async Task<bool> ConfirmedAsync(string accountId)
        {
            await using var db = fixture.Database.Open();
            var json = (await db.Contacts.SingleAsync(value => value.AccountId == accountId, Token)).ConfirmedGrantToJson;
            return json is not null && ProtocolModel.FromJson<ContactGrant>(json)!.Signatures.ContainsKey(currentId);
        }

        async Task<string[]> CurrentSendsAsync(string accountId)
        {
            await using var db = fixture.Database.Open();
            var requests = await db.MessageOutbox.Where(value => value.Recipient == accountId).Select(value => value.RequestJson).ToListAsync(Token);
            return requests.Where(json => ProtocolModel.FromJson<MessageSendRequest>(json)!.Envelope.FromDeviceId == currentId).Order(StringComparer.Ordinal).ToArray();
        }
    }

    static async Task PrepareSynchronizedContactAsync(TestClient fixture, AccountSigner peer)
    {
        await fixture.InitializeAsync();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        await ImportRemoteGrantsAsync(fixture);
    }

    // Model a contact synchronization from another authorized device: the entire
    // local database/key set remains intact, but this grant lacks our signature.
    static async Task ImportRemoteGrantsAsync(TestClient fixture)
    {
        var other = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId,
            [fixture.Client.Device!, other.Certificate], cancellationToken: Token);
        await using var db = fixture.Database.Open();
        foreach (var contact in await db.Contacts.ToListAsync(Token))
        {
            var grant = ProtocolModel.FromJson<ContactGrant>(contact.GrantToJson!)!;
            grant = grant with
            {
                Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(
                other.Certificate.GetDeviceId(TestNetwork.Context), [.. await other.SignAsync(grant.GetSigningInput(TestNetwork.Context), Token)])
            };
            contact.GrantToJson = contact.ConfirmedGrantToJson = grant.ToJson();
        }
        await db.MessageOutbox.Where(value => value.Recipient != fixture.Account.AccountId).ExecuteDeleteAsync(Token);
        await db.SaveChangesAsync(Token);
    }

    static async Task<List<MessageOutboxRecord>> InterruptDeliveryAsync(TestClient fixture, string peerId)
    {
        fixture.Relay.Handler = (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            if (IsPeerSend(request, peerId)) throw new HttpRequestException("Acceptance response lost");
            return Task.FromResult(response);
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () =>
        {
            await using var db = fixture.Database.Open();
            return await HasLocalSignatureAsync(fixture, confirmed: false)
                && await db.MessageOutbox.CountAsync(value => value.Recipient == peerId && value.State == MessageSendState.SubmissionUnknown, Token) == 2;
        });
        await fixture.Client.MessageManager.StopAsync(Token);
        fixture.Relay.Handler = null;
        await using var verify = fixture.Database.Open();
        return await verify.MessageOutbox.Where(value => value.Recipient == peerId && value.State == MessageSendState.SubmissionUnknown).ToListAsync(Token);
    }

    static async Task<bool> HasLocalSignatureAsync(TestClient fixture, bool confirmed)
    {
        await using var db = fixture.Database.Open();
        var contact = await db.Contacts.SingleAsync(Token);
        var json = confirmed ? contact.ConfirmedGrantToJson : contact.GrantToJson;
        return json is not null && ProtocolModel.FromJson<ContactGrant>(json)!.Signatures.ContainsKey(fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
    }

    static bool IsPeerSend(ObservedRequest request, string peerId) => request.Method == "message.send"
        && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.To == peerId;

    static bool IsMessageSend(ObservedRequest request, string messageId) => request.Method == "message.send"
        && ProtocolModel.FromJson<MessageSendRequest>(request.Body!)!.Envelope.MessageId == messageId;
}
