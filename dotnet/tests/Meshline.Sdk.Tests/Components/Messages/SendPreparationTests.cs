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
    public async Task Caller_cancellation_or_disposal_cancels_and_drains_both_queries(bool dispose)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var ownEntered = AsyncTest.Signal();
        var peerEntered = AsyncTest.Signal();
        var ownCanceled = AsyncTest.Signal();
        var peerCanceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                var own = QueriedAccount(request) == fixture.Account.AccountId;
                (own ? ownEntered : peerEntered).TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, own ? ownCanceled : peerCanceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var manager = fixture.Client.MessageManager;
        var sending = manager.SendMessageAsync(peer.AccountId, Draft(), caller.Token);
        Task disposed = Task.CompletedTask;
        try
        {
            await Task.WhenAll(ownEntered.Task, peerEntered.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (dispose)
                disposed = manager.DisposeAsync().AsTask();
            else
                caller.Cancel();
            await Task.WhenAll(ownCanceled.Task, peerCanceled.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(sending.IsCompleted);
            if (dispose)
                Assert.False(disposed.IsCompleted);
        }
        finally
        {
            caller.Cancel();
            release.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await disposed.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AssertNoDirectMessagesAsync(fixture);
    }

    [Fact]
    public async Task Self_send_resolves_device_state_only_once()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var queries = fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve");

        var queued = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);

        Assert.Equal(MessageSendState.Queued, queued.State);
        Assert.Equal(queries + 1, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
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

    static async Task AssertNoDirectMessagesAsync(TestClient fixture)
    {
        await using var database = fixture.Database.Open();
        Assert.False(await database.MessageOutbox.AnyAsync(message => message.IsDirect, Token));
        Assert.False(await database.Messages.AnyAsync(message => message.IsDirect, Token));
    }
}
