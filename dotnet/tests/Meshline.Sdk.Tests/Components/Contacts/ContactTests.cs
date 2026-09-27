using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Contacts;

public sealed class ContactTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

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
