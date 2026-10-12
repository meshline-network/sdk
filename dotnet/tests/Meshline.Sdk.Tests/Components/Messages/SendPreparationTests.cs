using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Meshline.Tests.Components.Messages;

public sealed class SendPreparationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static DirectMessageDraft Draft() => new() { Body = new() { ContentType = "text/plain", Text = "parallel preparation" } };
    static string QueriedAccount(ObservedRequest request) => request.Body is null
        ? RequestQuery.Parse(request)["account"] : ProtocolModel.FromJson<SignedDeviceStateQuery>(request.Body)!.Account;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_queries_overlap_and_enqueue_waits_for_both(bool ownFirst)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        await MakeDeviceStatesColdAsync(fixture);
        var ownEntered = AsyncTest.Signal();
        var peerEntered = AsyncTest.Signal();
        var ownRelease = AsyncTest.Signal();
        var peerRelease = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                var own = QueriedAccount(request) == fixture.Account.AccountId;
                (own ? ownEntered : peerEntered).TrySetResult();
                await (own ? ownRelease : peerRelease).Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };

        var sending = fixture.Client.MessageManager.SendMessageAsync(peer.AccountId, Draft(), Token);
        try
        {
            await Task.WhenAll(ownEntered.Task, peerEntered.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(sending.IsCompleted);
            (ownFirst ? ownRelease : peerRelease).SetResult();
            await AssertNoDirectMessagesAsync(fixture);
            Assert.False(sending.IsCompleted);
        }
        finally
        {
            ownRelease.TrySetResult();
            peerRelease.TrySetResult();
        }

        Assert.Equal(MessageSendState.Queued, (await sending).State);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "transport")]
    [InlineData(true, "transport")]
    [InlineData(false, "signature")]
    [InlineData(true, "signature")]
    public async Task Either_query_failing_prevents_enqueue_and_drains_the_other(bool ownFails, string failure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        await MakeDeviceStatesColdAsync(fixture);
        var failingAccount = ownFails ? fixture.Account.AccountId : peer.AccountId;
        var failingEntered = AsyncTest.Signal();
        var otherEntered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                if (QueriedAccount(request) == failingAccount)
                {
                    failingEntered.TrySetResult();
                    return failure switch
                    {
                        "missing" => OfflineRelay.Error("not_found"),
                        "transport" => throw new HttpRequestException("injected device lookup failure"),
                        _ => OfflineRelay.Json(fixture.Relay.Devices[failingAccount] with { AccountSignature = [.. new byte[64]] })
                    };
                }
                otherEntered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };

        var sending = fixture.Client.MessageManager.SendMessageAsync(peer.AccountId, Draft(), Token);
        try
        {
            await Task.WhenAll(failingEntered.Task, otherEntered.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            await AssertNoDirectMessagesAsync(fixture);
            Assert.False(sending.IsCompleted);
        }
        finally { release.TrySetResult(); }

        var error = await Record.ExceptionAsync(() => sending);
        switch (failure)
        {
            case "missing":
                Assert.IsType<InvalidOperationException>(error);
                Assert.Equal(ownFails ? "The account device state is unavailable." : "The recipient device state is unavailable.", error.Message);
                break;
            case "transport":
                Assert.IsType<HttpRequestException>(error);
                break;
            default:
                Assert.IsType<InvalidDataException>(error);
                break;
        }
        await AssertNoDirectMessagesAsync(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceling_send_or_disposing_message_manager_does_not_cancel_shared_device_queries(bool dispose)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        await MakeDeviceStatesColdAsync(fixture);
        var ownEntered = AsyncTest.Signal();
        var peerEntered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var shared = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                shared.Add(token);
                (QueriedAccount(request) == fixture.Account.AccountId ? ownEntered : peerEntered).TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var manager = fixture.Client.MessageManager;
        var sending = manager.SendMessageAsync(peer.AccountId, Draft(), caller.Token);
        try
        {
            await Task.WhenAll(ownEntered.Task, peerEntered.Task).WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (dispose)
                await manager.DisposeAsync();
            else
                caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.All(shared, token => Assert.False(token.IsCancellationRequested));
        }
        finally { release.TrySetResult(); }
        await AssertNoDirectMessagesAsync(fixture);
    }

    [Fact]
    public async Task Repeated_self_and_peer_sends_reuse_verified_device_states()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var queries = fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(MessageSendState.Queued, (await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token)).State);
            Assert.Equal(MessageSendState.Queued, (await fixture.Client.MessageManager.SendMessageAsync(peer.AccountId, Draft(), Token)).State);
        }
        Assert.Equal(queries, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("missing")]
    [InlineData("transport")]
    public async Task Revision_hints_and_failed_refreshes_do_not_block_sends_using_valid_state(string failure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var previous = fixture.Relay.Devices[peer.AccountId];
        ContactGrant grant;
        await using (var database = fixture.Database.Open())
        {
            var contact = await database.Contacts.SingleAsync(row => row.AccountId == peer.AccountId, Token);
            grant = ProtocolModel.FromJson<ContactGrant>(contact.GrantFromJson!)!;
            contact.RequiredDeviceRevision = previous.Revision + 1;
            await database.SaveChangesAsync(Token);
        }
        await fixture.ReopenAsync(); // Persisted state remains usable; refresh timing starts afresh.
        var manager = fixture.Client.DeviceManager;
        int Queries() => fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve" && QueriedAccount(request) == peer.AccountId);
        var count = Queries();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve" && QueriedAccount(request) == peer.AccountId)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return failure switch
                {
                    "missing" => OfflineRelay.Error("not_found"),
                    "transport" => throw new HttpRequestException("offline"),
                    _ => fixture.Relay.Respond(request)
                };
            }
            return fixture.Relay.Respond(request);
        };
        try
        {
            var sends = Enumerable.Range(0, 3).Select(_ => fixture.Client.MessageManager.SendMessageAsync(peer.AccountId, Draft(), Token));
            Assert.All(await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(5), Token), result => Assert.Equal(MessageSendState.Queued, result.State));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(count + 1, Queries());
            var joined = manager.RefreshDeviceStateAsync(grant, Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(30));
            release.TrySetResult();
            if (failure == "missing") Assert.Null(await joined);
            else if (failure == "transport") await Assert.ThrowsAsync<HttpRequestException>(() => joined);
            else await Assert.ThrowsAsync<InvalidDataException>(() => joined);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(previous.Revision, (await manager.GetDeviceStateAsync(grant, Token))!.Revision);
            Assert.Equal(MessageSendState.Queued, (await fixture.Client.MessageManager.SendMessageAsync(peer.AccountId, Draft(), Token)).State);
            Assert.Equal(count + 1, Queries());

            var updated = previous with { Revision = previous.Revision + 1 };
            fixture.Relay.Devices[peer.AccountId] = updated with { AccountSignature = [.. peer.Sign(updated.GetSigningInput(TestNetwork.Context))] };
            entered = AsyncTest.Signal();
            release = AsyncTest.Signal();
            failure = "success";
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(count + 1, Queries()); // No timed retry.
            Assert.Equal(previous.Revision, (await manager.GetDeviceStateAsync(grant, Token))!.Revision);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            joined = manager.RefreshDeviceStateAsync(grant, Token);
            release.TrySetResult();
            Assert.Equal(updated.Revision, (await joined)!.Revision);
            Assert.Equal(count + 2, Queries());
            for (var i = 0; i < 3; i++)
                Assert.Equal(updated.Revision, (await manager.GetDeviceStateAsync(grant, Token))!.Revision);
            Assert.Equal(count + 2, Queries());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Newly_revoked_contact_signer_cannot_authorize_enqueue()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var previous = fixture.Relay.Devices[peer.AccountId];
        var revoked = previous with { Revision = previous.Revision + 1, Certificates = [] };
        fixture.Relay.Devices[peer.AccountId] = revoked with { AccountSignature = [.. peer.Sign(revoked.GetSigningInput(TestNetwork.Context))] };

        await fixture.Client.DeviceManager.RefreshDeviceStateAsync(peer.AccountId, Token);
        await Assert.ThrowsAsync<CryptographicException>(() => fixture.Client.MessageManager.SendMessageAsync(peer.AccountId, Draft(), Token));

        await AssertNoDirectMessagesAsync(fixture);
    }

    [Fact]
    public async Task Newly_revoked_local_device_cannot_enqueue_after_peer_query_succeeds()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var previous = fixture.Relay.Devices[fixture.Account.AccountId];
        var revoked = previous with { Revision = previous.Revision + 1, Certificates = [] };
        fixture.Relay.Devices[fixture.Account.AccountId] = revoked with { AccountSignature = [.. fixture.Account.Sign(revoked.GetSigningInput(TestNetwork.Context))] };
        await MakeDeviceStatesColdAsync(fixture);
        var peerState = fixture.Relay.Devices[peer.AccountId];
        peerState = peerState with { Revision = peerState.Revision + 1 };
        fixture.Relay.Devices[peer.AccountId] = peerState with { AccountSignature = [.. peer.Sign(peerState.GetSigningInput(TestNetwork.Context))] };
        var releaseOwn = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve" && QueriedAccount(request) == fixture.Account.AccountId)
                await releaseOwn.Task.WaitAsync(token);
            return fixture.Relay.Respond(request);
        };

        var sending = fixture.Client.MessageManager.SendMessageAsync(peer.AccountId, Draft(), Token);
        try
        {
            await AsyncTest.UntilAsync(async () =>
            {
                await using var database = fixture.Database.Open();
                return await database.DeviceStates.AnyAsync(state => state.AccountId == peer.AccountId && state.Revision == peerState.Revision, Token);
            });
            Assert.False(sending.IsCompleted);
        }
        finally { releaseOwn.TrySetResult(); }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sending);
        Assert.Equal(revoked.Revision, fixture.Client.DeviceState!.Revision);
        await AssertNoDirectMessagesAsync(fixture);
    }

    static async Task MakeDeviceStatesColdAsync(TestClient fixture)
    {
        await using (var database = fixture.Database.Open())
            await database.DeviceStates.ExecuteDeleteAsync(Token);
        await fixture.ReopenAsync();
    }

    static async Task AssertNoDirectMessagesAsync(TestClient fixture)
    {
        await using var database = fixture.Database.Open();
        Assert.False(await database.MessageOutbox.AnyAsync(message => message.IsDirect, Token));
        Assert.False(await database.Messages.AnyAsync(message => message.IsDirect, Token));
    }
}
