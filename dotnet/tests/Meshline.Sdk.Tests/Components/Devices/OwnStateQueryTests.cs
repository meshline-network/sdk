using Meshline.Components;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Tests.Components.Devices;

public sealed class OwnStateQueryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static int Queries(TestClient fixture) => fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve");

    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("failure")]
    [InlineData("signature")]
    public async Task Concurrent_own_queries_share_one_lookup_but_completed_results_are_not_cached(string outcome)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.DeviceManager;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var changes = 0;
        manager.DeviceStateChanged += (_, _) => Interlocked.Increment(ref changes);
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return outcome switch
                {
                    "missing" => OfflineRelay.Error("not_found"),
                    "failure" => throw new HttpRequestException("injected lookup failure"),
                    "signature" => OfflineRelay.Json(fixture.Client.DeviceState! with { AccountSignature = [.. new byte[64]] }),
                    _ => fixture.Relay.Respond(request)
                };
            }
            return fixture.Relay.Respond(request);
        };
        var count = Queries(fixture);
        var queries = Enumerable.Range(0, 12).Select(index => index % 2 == 0
            ? manager.GetDeviceStateAsync(cancellationToken: Token)
            : manager.GetDeviceStateAsync(fixture.Account.AccountId, Token)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(count + 1, Queries(fixture));
            Assert.All(queries, query => Assert.False(query.IsCompleted));
        }
        finally { release.TrySetResult(); }

        foreach (var query in queries)
        {
            if (outcome == "failure")
                await Assert.ThrowsAsync<HttpRequestException>(() => query);
            else if (outcome == "signature")
                await Assert.ThrowsAsync<InvalidDataException>(() => query);
            else if (outcome == "missing")
                Assert.Null(await query);
            else
                Assert.Equal(fixture.Account.AccountId, (await query)!.Account);
        }
        Assert.Equal(0, changes);
        Assert.Equal(count + 1, Queries(fixture));

        fixture.Relay.Handler = null;
        Assert.NotNull(await manager.GetDeviceStateAsync(cancellationToken: Token));
        Assert.Equal(count + 2, Queries(fixture));
    }

    [Fact]
    public async Task Canceling_one_waiter_preserves_the_other_waiter_and_shared_transport()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var sharedToken = CancellationToken.None;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                sharedToken = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var manager = fixture.Client.DeviceManager;
        var first = manager.GetDeviceStateAsync(cancellationToken: caller.Token);
        var second = manager.GetDeviceStateAsync(cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.False(sharedToken.IsCancellationRequested);
            Assert.False(second.IsCompleted);
        }
        finally { release.TrySetResult(); }

        Assert.NotNull(await second);
    }

    [Fact]
    public async Task Last_waiter_cancellation_drains_transport_without_removing_a_replacement_query()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var firstEntered = AsyncTest.Signal();
        var firstCanceled = AsyncTest.Signal();
        var firstRelease = AsyncTest.Signal();
        var secondEntered = AsyncTest.Signal();
        var secondRelease = AsyncTest.Signal();
        var count = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    firstEntered.TrySetResult();
                    await AsyncTest.WaitForCancellationAndCleanupAsync(token, firstCanceled, firstRelease.Task);
                }
                else
                {
                    secondEntered.TrySetResult();
                    await secondRelease.Task.WaitAsync(token);
                }
            }
            return fixture.Relay.Respond(request);
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var manager = fixture.Client.DeviceManager;
        var first = manager.GetDeviceStateAsync(cancellationToken: caller.Token);
        Task<AccountDeviceState?>? second = null;
        Task<AccountDeviceState?>? third = null;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            caller.Cancel();
            await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(first.IsCompleted);
            second = manager.GetDeviceStateAsync(cancellationToken: Token);
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            firstRelease.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            third = manager.GetDeviceStateAsync(cancellationToken: Token);
            Assert.Equal(2, Volatile.Read(ref count));
        }
        finally
        {
            caller.Cancel();
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
        }

        Assert.NotNull(await second!);
        Assert.NotNull(await third!);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Disposal_cancels_and_drains_shared_query_and_all_waiters()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                entered.TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        var first = manager.GetDeviceStateAsync(cancellationToken: Token);
        var second = manager.GetDeviceStateAsync(cancellationToken: Token);
        Task disposed;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            disposed = manager.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(disposed.IsCompleted);
        }
        finally { release.TrySetResult(); }

        await disposed.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.GetDeviceStateAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task Different_relays_do_not_share_or_block_queries()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var other = new OfflineRelay("other.test");
        fixture.Relay.LinkTo(other);
        other.Devices[fixture.Account.AccountId] = fixture.Client.DeviceState!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        var first = manager.GetDeviceStateAsync(cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await fixture.Client.AccountManager.PublishRouteAsync(other.RelayId, TimeSpan.FromDays(7), cancellationToken: Token);
            Assert.NotNull(await manager.GetDeviceStateAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.False(first.IsCompleted);
            Assert.Single(other.Requests, request => request.Method == "device.state.resolve");
        }
        finally { release.TrySetResult(); }
        Assert.NotNull(await first);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("grant")]
    [InlineData("invite")]
    public async Task Peer_queries_remain_independent_including_signed_authorization(string evidence)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var signer = fixture.Relay.AddPeer(peer);
        var deviceId = signer.Certificate.GetDeviceId(TestNetwork.Context);
        var grant = new ContactGrant
        {
            Grantor = peer.AccountId,
            Grantee = fixture.Account.AccountId,
            ExpiresAt = Clock.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
        };
        grant = grant with { Signatures = grant.Signatures.Add(deviceId, [.. await signer.SignAsync(grant.GetSigningInput(TestNetwork.Context), Token)]) };
        var invite = new ContactInvite
        {
            Inviter = peer.AccountId,
            ExpiresAt = grant.ExpiresAt!.Value,
            SignerDeviceId = deviceId,
            DeviceSignature = []
        };
        invite = invite with { DeviceSignature = [.. await signer.SignAsync(invite.GetSigningInput(TestNetwork.Context), Token)] };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var count = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                if (Interlocked.Increment(ref count) == 3)
                    entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        Task<AccountDeviceState?> ReadPeerAsync() => evidence switch
        {
            "grant" => manager.GetDeviceStateAsync(grant, Token),
            "invite" => manager.GetDeviceStateAsync(invite, Token),
            _ => manager.GetDeviceStateAsync(peer.AccountId, Token)
        };
        var own = manager.GetDeviceStateAsync(cancellationToken: Token);
        var firstPeer = ReadPeerAsync();
        var secondPeer = ReadPeerAsync();
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); }
        finally { release.TrySetResult(); }

        Assert.Equal(fixture.Account.AccountId, (await own)!.Account);
        Assert.Equal(peer.AccountId, (await firstPeer)!.Account);
        Assert.Equal(peer.AccountId, (await secondPeer)!.Account);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Separate_device_managers_do_not_share_queries_even_for_the_same_account()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var other = new DeviceManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Client.AccountManager, fixture.Account, fixture.Protector);
        await other.InitializeAsync(Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var queries = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                if (Interlocked.Increment(ref queries) == 2)
                    entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };

        var first = fixture.Client.DeviceManager.GetDeviceStateAsync(cancellationToken: Token);
        var second = other.GetDeviceStateAsync(cancellationToken: Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); }
        finally { release.TrySetResult(); }

        Assert.NotNull(await first);
        Assert.NotNull(await second);
        Assert.Equal(2, queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_or_certificate_renewal_prevents_joining_an_older_query(bool publish)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var previous = fixture.Client.DeviceState!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var queries = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve" && Interlocked.Increment(ref queries) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfflineRelay.Json(previous);
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        var old = manager.GetDeviceStateAsync(cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (publish)
                await manager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
            else
                await manager.RenewDeviceAsync(TimeSpan.FromDays(20), Token);
            var latest = await manager.GetDeviceStateAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(publish ? previous.Revision + 1 : previous.Revision, latest!.Revision);
            Assert.False(old.IsCompleted);
        }
        finally { release.TrySetResult(); }

        if (publish)
            await Assert.ThrowsAsync<InvalidDataException>(() => old);
        else
            Assert.NotNull(await old);
        Assert.Equal(2, queries);
        await using var database = fixture.Database.Open();
        Assert.Equal(manager.DeviceState!.Revision, (await database.DeviceStates.SingleAsync(state => state.AccountId == fixture.Account.AccountId, Token)).Revision);
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("removal")]
    [InlineData("establishment")]
    public async Task Explicit_operations_do_not_join_a_request_started_before_remote_state_changed(string operation)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var previous = fixture.Client.DeviceState!;
        var remoteDevice = new DeviceSigner(fixture.Account, Clock.Provider).Certificate;
        var newer = previous with { Revision = previous.Revision + 1, Certificates = previous.Certificates.Add(remoteDevice) };
        newer = newer with { AccountSignature = [.. fixture.Account.Sign(newer.GetSigningInput(TestNetwork.Context))] };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var queries = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve" && Interlocked.Increment(ref queries) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfflineRelay.Json(previous);
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        var old = manager.GetDeviceStateAsync(cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            fixture.Relay.Devices[fixture.Account.AccountId] = newer;
            if (operation == "removal")
            {
                await manager.RemoveDeviceAsync(remoteDevice.GetDeviceId(TestNetwork.Context), Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.DoesNotContain(manager.DeviceState!.Certificates, certificate => certificate.GetDeviceId(TestNetwork.Context) == remoteDevice.GetDeviceId(TestNetwork.Context));
                Assert.Equal(newer.Revision + 1, fixture.Relay.Devices[fixture.Account.AccountId].Revision);
            }
            else if (operation == "establishment")
            {
                await fixture.Client.EstablishAccountAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.Equal(newer.Revision, manager.DeviceState!.Revision);
            }
            else
            {
                var latest = await manager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.Equal(newer.Revision, latest!.Revision);
            }
            Assert.False(old.IsCompleted);
        }
        finally { release.TrySetResult(); }

        await Assert.ThrowsAsync<InvalidDataException>(() => old);
        Assert.Equal(operation == "removal" ? newer.Revision + 1 : newer.Revision, manager.DeviceState!.Revision);
        Assert.Equal(2, queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_background_sync_cancels_only_queries_without_foreground_waiters(bool foreground)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var queries = 0;
        var sharedToken = CancellationToken.None;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve" && Interlocked.Increment(ref queries) > 1)
            {
                sharedToken = token;
                entered.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    await release.Task.WaitAsync(Token);
                    throw;
                }
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        await manager.StartAsync(Token);
        Task<AccountDeviceState?>? read = null;
        Task stopped;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (foreground)
                read = manager.GetDeviceStateAsync(cancellationToken: Token);
            stopped = manager.StopAsync(Token);
            if (foreground)
            {
                await stopped.WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.False(sharedToken.IsCancellationRequested);
                Assert.False(read!.IsCompleted);
            }
            else
            {
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.False(stopped.IsCompleted);
            }
        }
        finally { release.TrySetResult(); }

        await stopped.WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (read is not null)
            Assert.NotNull(await read);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task Relay_notification_requires_a_new_lookup_while_an_older_one_is_in_flight()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var previous = fixture.Client.DeviceState!;
        var newer = previous with { Revision = previous.Revision + 1 };
        newer = newer with { AccountSignature = [.. fixture.Account.Sign(newer.GetSigningInput(TestNetwork.Context))] };
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var observed = AsyncTest.Signal();
        var rejectedOld = AsyncTest.Signal();
        var queries = 0;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve" && Interlocked.Increment(ref queries) == 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfflineRelay.Json(previous);
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        manager.BackgroundError += (_, error) =>
        {
            if (error.Error is InvalidDataException)
                rejectedOld.TrySetResult();
            else
                rejectedOld.TrySetException(error.Error);
        };
        var connected = AsyncTest.Signal();
        var relay = await fixture.Pool.GetAsync(fixture.Relay.RelayId, manager, Token);
        relay.SocketConnected += (_, _) => connected.TrySetResult();
        await manager.StartAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            relay.NotificationReceived += (_, request) =>
            {
                if (request.Method == "device.state.changed")
                    observed.TrySetResult();
            };
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            fixture.Relay.Devices[fixture.Account.AccountId] = newer;
            fixture.Relay.Sockets.Last().Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "device.state.changed", @params = new { revision = newer.Revision } }));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var latest = await manager.GetDeviceStateAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(newer.Revision, latest!.Revision);
        }
        finally { release.TrySetResult(); }

        await rejectedOld.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await manager.StopAsync(Token);
        Assert.Equal(newer.Revision, manager.DeviceState!.Revision);
        Assert.Equal(3, queries);
    }
}
