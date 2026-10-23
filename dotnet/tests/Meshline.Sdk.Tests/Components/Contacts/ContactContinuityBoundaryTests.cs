using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Contacts;

public sealed class ContactContinuityBoundaryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_installation_can_remove_its_own_new_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var remoteId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using var pool = fixture.Relay.Pool(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), database.Options, pool, fixture.Protector, fixture.Account);
        await client.InitializeAsync(Token);
        await client.EstablishAccountAsync(cancellationToken: Token);
        var selectedId = client.Device!.GetDeviceId(TestNetwork.Context);

        await client.DeviceManager.RemoveDeviceAsync(selectedId, Token);

        Assert.DoesNotContain(client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
        Assert.Contains(client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == remoteId);
        await using var verify = database.Open();
        Assert.Empty(await verify.Contacts.ToListAsync(Token));
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(MessageSendState.Queued)]
    [InlineData(MessageSendState.SubmissionUnknown)]
    [InlineData(MessageSendState.RelayAccepted)]
    [InlineData(MessageSendState.TargetAccepted)]
    [InlineData(MessageSendState.Failed)]
    [InlineData(MessageSendState.Canceled)]
    public async Task Outgoing_requests_protect_their_signer_until_resolved(MessageSendState state)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock).Certificate;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, remaining], cancellationToken: Token);
        var request = await fixture.Client.MessageManager.AddContactAsync(peer.AccountId, cancellationToken: Token);
        string ciphertext;
        await using (var db = fixture.Database.Open())
        {
            var pending = await db.ContactRequests.SingleAsync(Token);
            pending.SendState = state;
            var outbox = await db.MessageOutbox.SingleAsync(value => value.MessageId == request.MessageId, Token);
            outbox.State = state;
            ciphertext = outbox.RequestJson;
            await db.SaveChangesAsync(Token);
            Assert.Empty(await db.Contacts.ToListAsync(Token));
        }
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var mutation = fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token);

        if (state is MessageSendState.Failed or MessageSendState.Canceled)
        {
            await mutation;
            Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => mutation);
            Assert.Contains("replacement_grant_not_confirmed", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        }
        await using var verify = fixture.Database.Open();
        Assert.Equal(ciphertext, (await verify.MessageOutbox.SingleAsync(value => value.MessageId == request.MessageId, Token)).RequestJson);
        Assert.Equal(request.MessageId, (await verify.ContactRequests.SingleAsync(Token)).MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_coordinates_contact_writes_but_not_ordinary_message_enqueue(bool unknownOutcome)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock).Certificate;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, remaining], cancellationToken: Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                if (unknownOutcome) throw new HttpRequestException("unknown publication");
            }
            return fixture.Relay.Respond(request);
        };
        var removal = fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.MessageManager.AddContactAsync(peer.AccountId, cancellationToken: Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.Contains("pending device publication", error.Message, StringComparison.Ordinal);
            var sent = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId,
                new() { Body = new() { ContentType = "text/plain", Text = "unrelated message" } }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(MessageSendState.Queued, sent.State);
        }
        finally { release.TrySetResult(); }
        if (unknownOutcome)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => removal);
            string pendingRequest;
            await using (var database = fixture.Database.Open())
                pendingRequest = (await database.SignedRequests.SingleAsync(value => value.Method == "device.state.publish" && value.Pending, Token)).DocumentJson;
            await fixture.ReopenAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.MessageManager.AddContactAsync(peer.AccountId, cancellationToken: Token));
            fixture.Relay.Handler = null;
            fixture.Relay.Requests.Clear();
            await fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
            Assert.Equal(pendingRequest, Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish").Body);
        }
        else
        {
            await removal;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Client.MessageManager.AddContactAsync(peer.AccountId, cancellationToken: Token));
        }
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.ContactRequests.ToListAsync(Token));
        Assert.Empty(await verify.MessageOutbox.Where(value => value.Recipient == peer.AccountId).ToListAsync(Token));
    }

    [Fact]
    public async Task A_newer_grant_does_not_hide_an_older_confirmed_grant_that_depends_on_the_removed_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, remaining.Certificate], cancellationToken: Token);
        await using (var database = fixture.Database.Open())
        {
            var contact = await database.Contacts.SingleAsync(Token);
            contact.ConfirmedGrantToJson = contact.GrantToJson;
            var newer = ProtocolModel.FromJson<ContactGrant>(contact.GrantToJson!)!;
            newer = newer with { ExpiresAt = newer.ExpiresAt + 60 };
            newer = newer with
            {
                Signatures = newer.Signatures.Clear().Add(remaining.Certificate.GetDeviceId(TestNetwork.Context),
                [.. await remaining.SignAsync(newer.GetSigningInput(TestNetwork.Context), Token)])
            };
            contact.GrantToJson = newer.ToJson();
            await database.SaveChangesAsync(Token);
        }
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token));

        Assert.Contains("replacement_grant_not_confirmed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
    }

    [Fact]
    public async Task A_target_accepted_outgoing_request_can_retain_a_remaining_device_signature()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, remaining.Certificate], cancellationToken: Token);
        await fixture.Client.MessageManager.AddContactAsync(peer.AccountId, cancellationToken: Token);
        await using (var database = fixture.Database.Open())
        {
            var request = await database.ContactRequests.SingleAsync(Token);
            var consent = ProtocolModel.FromJson<ContactConsent>(request.ConsentJson)!;
            var grant = consent.Grant;
            grant = grant with
            {
                Signatures = grant.Signatures.Add(remaining.Certificate.GetDeviceId(TestNetwork.Context),
                [.. await remaining.SignAsync(grant.GetSigningInput(TestNetwork.Context), Token)])
            };
            request.ConsentJson = (consent with { Grant = grant }).ToJson();
            request.SendState = MessageSendState.TargetAccepted;
            // The retained contact request must be sufficient even after terminal
            // outbox retention has pruned the original encrypted delivery.
            database.MessageOutbox.Remove(await database.MessageOutbox.SingleAsync(value => value.MessageId == request.MessageId, Token));
            await database.SaveChangesAsync(Token);
        }
        await fixture.ReopenAsync();

        await fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token);

        Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
        Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == remaining.Certificate.GetDeviceId(TestNetwork.Context));
    }
}
