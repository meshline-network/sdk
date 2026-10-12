using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Contacts;

public sealed class ContactTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("success")]
    [InlineData("rollback")]
    [InlineData("observer_failure")]
    public async Task Received_consent_keeps_the_device_cache_consistent_with_the_commit(string outcome)
    {
        using var time = Clock.Use(new ManualClock());
        await using var sender = new TestClient();
        await sender.InitializeAsync();
        await using var receiver = new TestClient();
        await receiver.InitializeAsync();
        sender.Relay.Devices[receiver.Account.AccountId] = receiver.Client.DeviceState!;
        var outgoing = await sender.Client.MessageManager.AddContactAsync(receiver.Account.AccountId, cancellationToken: Token);
        MessageSendRequest request;
        await using (var database = sender.Database.Open())
            request = ProtocolModel.FromJson<MessageSendRequest>((await database.MessageOutbox.SingleAsync(row => row.MessageId == outgoing.MessageId, Token)).RequestJson)!;
        var manager = receiver.Client.MessageManager;
        var resolving = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var observerFailure = new InvalidOperationException("observer failed");
        if (outcome == "observer_failure") manager.TimelineChanged += (_, _) => throw observerFailure;
        if (outcome == "rollback")
        {
            await using var database = receiver.Database.Open();
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_consent BEFORE INSERT ON Messages BEGIN SELECT RAISE(ABORT, 'injected storage failure'); END;", Token);
        }
        receiver.Relay.Handler = async (http, token) =>
        {
            if (http.Method == "device.state.resolve")
            {
                resolving.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            if (http.Method == "message.timeline.sync") return OfflineRelay.Json(new MessageTimelinePage
            {
                Items = long.Parse(RequestQuery.Parse(http)["after"]) < 0 ? [new MessageTimelineEntry
                {
                    Sequence = 0, Envelope = request.Envelope, KeyBox = request.RecipientBoxes[0],
                    AcceptedAt = Clock.UtcNow.ToUnixTimeSeconds()
                }] : [],
                Certificates = [sender.Client.Device!],
                HasMore = false
            });
            return receiver.Relay.Respond(http);
        };
        try
        {
            if (outcome == "rollback")
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => manager.SynchronizeAsync(receiver.Relay.RelayId, Token));
                await using var rolledBack = receiver.Database.Open();
                Assert.False(await rolledBack.DeviceStates.AnyAsync(row => row.AccountId == sender.Account.AccountId, Token));
                Assert.Empty(await rolledBack.ContactRequests.ToListAsync(Token));
                await using var sent = sender.Database.Open();
                var sentConsent = ProtocolModel.FromJson<ContactConsent>((await sent.ContactRequests.SingleAsync(Token)).ConsentJson)!;
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
                var lookup = receiver.Client.DeviceManager.GetDeviceStateAsync(sentConsent.Grant, caller.Token);
                await Task.WhenAny(lookup, resolving.Task).WaitAsync(TimeSpan.FromSeconds(5), Token);
                Assert.False(lookup.IsCompleted); // Rolled-back state must not be available from the cache.
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
                return;
            }
            if (outcome == "observer_failure")
                Assert.Same(observerFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SynchronizeAsync(receiver.Relay.RelayId, Token)));
            else
                await manager.SynchronizeAsync(receiver.Relay.RelayId, Token);
            await using var database = receiver.Database.Open();
            var consent = ProtocolModel.FromJson<ContactConsent>((await database.ContactRequests.SingleAsync(row => row.AccountId == sender.Account.AccountId, Token)).ConsentJson)!;
            var state = await receiver.Client.DeviceManager.GetDeviceStateAsync(consent.Grant, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(consent.DeviceState.ToJson(), state!.ToJson());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Contact_request_is_idempotent_and_can_be_canceled()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        var manager = fixture.Client.MessageManager;
        var first = await manager.AddContactAsync(peer.AccountId, "hello", Token);
        var second = await manager.AddContactAsync(peer.AccountId, "hello again", Token);

        Assert.Equal(first.MessageId, second.MessageId);

        await using (var requests = await manager.GetContactRequestsAsync(cancellationToken: Token))
            Assert.Single(await requests.ReadNextAsync(10, Token));

        Assert.True(await manager.CancelMessageAsync(first.MessageId!, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.AddContactAsync(fixture.Account.AccountId, cancellationToken: Token));
    }

    [Fact]
    public async Task Accepted_contact_supports_alias_queries_expiry_and_removal()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var manager = fixture.Client.MessageManager;
        var contact = await manager.SetAliasAsync(peer.AccountId, "Friend", Token);

        Assert.Equal(ContactGrantState.Valid, contact.GrantFromContact);

        await using (var reader = await manager.GetContactsAsync("Friend", Token))
            Assert.Single(await reader.ReadNextAsync(10, Token));
        fixture.Relay.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(ContactGrantState.Expired, (await manager.GetContactAsync(peer.AccountId, Token))!.GrantFromContact);

        await manager.RemoveContactAsync(peer.AccountId, Token);

        Assert.Null(await manager.GetContactAsync(peer.AccountId, Token));

        await using var db = fixture.Database.Open();

        Assert.Equal(ContactRelationshipState.Deleted, (await db.Contacts.SingleAsync(Token)).State);
    }

    [Fact]
    public async Task Contact_invitation_signature_and_expiry_use_scoped_time()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var expires = fixture.Relay.Clock.GetUtcNow().AddMinutes(1);
        var invite = await fixture.Client.MessageManager.CreateInviteAsync(expires, Token);

        Assert.Null(invite.Validate());
        Assert.True(Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Verify(invite.DeviceSignature.AsSpan(), fixture.Client.Device!.SigningPublicKey.AsSpan(), invite.GetSigningInput(TestNetwork.Context)));

        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.NotNull(invite.Validate());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.MessageManager.CreateInviteAsync(expires, Token));
    }
}
