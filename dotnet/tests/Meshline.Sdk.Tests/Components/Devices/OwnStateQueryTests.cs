using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Tests.Components.Devices;

public sealed class OwnStateQueryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static int Queries(TestClient fixture) => fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve");

    [Fact]
    public async Task Restart_reuses_verified_state_but_resets_in_memory_refresh_timing()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var count = Queries(fixture);
        var changes = 0;
        fixture.Client.DeviceManager.DeviceStateChanged += (_, _) => changes++;
        for (var i = 0; i < 12; i++)
            Assert.NotNull(await fixture.Client.DeviceManager.GetDeviceStateAsync(cancellationToken: Token));
        Assert.Equal(count, Queries(fixture));
        await fixture.ReopenAsync();
        Assert.Equal(count, Queries(fixture));
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Client.DeviceManager.DeviceStateChanged += (_, _) => changes++;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        try
        {
            for (var i = 0; i < 12; i++)
                Assert.NotNull(await fixture.Client.DeviceManager.GetDeviceStateAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(count + 1, Queries(fixture));
            var joined = fixture.Client.DeviceManager.RefreshDeviceStateAsync(cancellationToken: Token);
            release.TrySetResult();
            await joined;
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("failure")]
    [InlineData("signature")]
    public async Task Reads_refresh_one_hour_after_success_or_one_minute_after_failure_without_timers(string outcome)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.DeviceManager;
        var original = manager.DeviceState!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var errors = 0;
        var changes = 0;
        manager.BackgroundError += (_, _) => Interlocked.Increment(ref errors);
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
                    "failure" => throw new HttpRequestException("offline"),
                    "signature" => OfflineRelay.Json(original with { AccountSignature = [.. new byte[64]] }),
                    _ => fixture.Relay.Respond(request)
                };
            }
            return fixture.Relay.Respond(request);
        };
        var count = Queries(fixture);
        fixture.Relay.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(count, Queries(fixture)); // elapsed time alone does not start any work
        try
        {
            var reads = Enumerable.Range(0, 12).Select(_ => manager.GetDeviceStateAsync(cancellationToken: Token)).ToArray();
            Assert.All(await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5), Token), state => Assert.Same(original, state));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(count + 1, Queries(fixture));
            Assert.Same(original, await manager.GetDeviceStateAsync(cancellationToken: Token));
            var joined = manager.RefreshDeviceStateAsync(cancellationToken: Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(30)); // Intervals begin at completion, not request start.
            release.TrySetResult();
            if (outcome == "failure")
                await Assert.ThrowsAsync<HttpRequestException>(() => joined);
            else if (outcome == "signature")
                await Assert.ThrowsAsync<InvalidDataException>(() => joined);
            else if (outcome == "missing")
                Assert.Null(await joined);
            else
                Assert.Equal(original.ToJson(), (await joined)!.ToJson());
            var completed = Queries(fixture);
            var interval = outcome == "success" ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1);
            fixture.Relay.Clock.Advance(interval - TimeSpan.FromSeconds(1));
            for (var i = 0; i < 12; i++)
                Assert.Equal(original.ToJson(), (await manager.GetDeviceStateAsync(cancellationToken: Token))!.ToJson());
            Assert.Equal(completed, Queries(fixture));
            Assert.Equal(0, changes);
            Assert.Equal(outcome is "failure" or "signature" ? 1 : 0, errors);
            fixture.Relay.Handler = null;
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(completed, Queries(fixture));
            Assert.NotNull(await manager.GetDeviceStateAsync(cancellationToken: Token));
            await AsyncTest.UntilAsync(() => Queries(fixture) == completed + 1);
        }
        finally
        {
            release.TrySetResult();
            await manager.StopAsync(Token);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("failure")]
    [InlineData("signature")]
    public async Task Cold_queries_coalesce_and_failures_back_off_without_becoming_not_found(string outcome)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return outcome switch
                {
                    "missing" => OfflineRelay.Error("not_found"),
                    "failure" => throw new HttpRequestException("offline"),
                    "signature" => OfflineRelay.Json(fixture.Relay.Devices[peer.AccountId] with { AccountSignature = [.. new byte[64]] }),
                    _ => fixture.Relay.Respond(request)
                };
            }
            return fixture.Relay.Respond(request);
        };
        var count = Queries(fixture);
        var reads = Enumerable.Range(0, 12).Select(_ => fixture.Client.DeviceManager.GetDeviceStateAsync(peer.AccountId, Token)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.All(reads, read => Assert.False(read.IsCompleted));
        }
        finally { release.TrySetResult(); }
        foreach (var read in reads)
            await CheckAsync(read);
        for (var i = 0; i < 3; i++)
            await CheckAsync(fixture.Client.DeviceManager.GetDeviceStateAsync(peer.AccountId, Token));
        Assert.Equal(count + 1, Queries(fixture));

        async Task CheckAsync(Task<AccountDeviceState?> read)
        {
            if (outcome == "failure") await Assert.ThrowsAsync<HttpRequestException>(() => read);
            else if (outcome == "signature") await Assert.ThrowsAsync<InvalidDataException>(() => read);
            else if (outcome == "missing") Assert.Null(await read);
            else Assert.Equal(peer.AccountId, (await read)!.Account);
        }
    }

    [Fact]
    public async Task Caller_cancellation_does_not_cancel_shared_refresh_even_when_all_waiters_leave()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var shared = CancellationToken.None;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                shared = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var manager = fixture.Client.DeviceManager;
        var count = Queries(fixture);
        var first = manager.RefreshDeviceStateAsync(cancellationToken: caller.Token);
        Task<AccountDeviceState?> second;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(shared.IsCancellationRequested);
            second = manager.RefreshDeviceStateAsync(cancellationToken: Token);
            Assert.False(second.IsCompleted);
            Assert.NotNull(await manager.GetDeviceStateAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.Equal(count + 1, Queries(fixture));
        }
        finally { release.TrySetResult(); }
        Assert.NotNull(await second);
    }

    [Theory]
    [InlineData("manager-stop")]
    [InlineData("client-stop")]
    [InlineData("manager-dispose")]
    [InlineData("client-dispose")]
    public async Task Stop_and_disposal_cancel_and_drain_refreshes(string action)
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
        var read = manager.RefreshDeviceStateAsync(cancellationToken: Token);
        Task stopped;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            stopped = action switch
            {
                "manager-dispose" => manager.DisposeAsync().AsTask(),
                "client-dispose" => fixture.Client.DisposeAsync().AsTask(),
                "client-stop" => fixture.Client.StopAsync(Token),
                _ => manager.StopAsync(Token)
            };
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(stopped.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await stopped.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        if (action.EndsWith("dispose", StringComparison.Ordinal)) await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.GetDeviceStateAsync(cancellationToken: Token));
        else Assert.NotNull(await manager.GetDeviceStateAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task Accounts_and_query_permissions_are_isolated_but_compatible_peer_queries_share_results()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var signer = fixture.Relay.AddPeer(peer);
        var grant = await GrantAsync(fixture, peer, signer);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve" && request.Body is not null)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        var count = Queries(fixture);
        var first = manager.GetDeviceStateAsync(grant, Token);
        var second = manager.GetDeviceStateAsync(grant, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.NotNull(await manager.GetDeviceStateAsync(cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal((await first)!.ToJson(), (await second)!.ToJson());
        Assert.NotNull(await manager.GetDeviceStateAsync(grant, Token));
        Assert.Equal(count + 1, Queries(fixture));
        // Public discovery needs its own authorization observation; private evidence must not seed it.
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "device.state.resolve" ? OfflineRelay.Error("forbidden") : fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<Meshline.Transport.RelayException>(() => manager.GetDeviceStateAsync(peer.AccountId, Token));
        Assert.NotNull(await manager.GetDeviceStateAsync(grant, Token));
        await fixture.ReopenAsync();
        Assert.NotNull(await fixture.Client.DeviceManager.GetDeviceStateAsync(grant, Token));
    }

    [Fact]
    public async Task Publication_wins_over_a_late_old_response()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.DeviceManager;
        var previous = manager.DeviceState!;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfflineRelay.Json(previous);
            }
            return fixture.Relay.Respond(request);
        };
        var old = manager.RefreshDeviceStateAsync(cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await manager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
            Assert.Equal(previous.Revision + 1, (await manager.GetDeviceStateAsync(cancellationToken: Token))!.Revision);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => old);
        await using var database = fixture.Database.Open();
        Assert.Equal(previous.Revision + 1, (await database.DeviceStates.SingleAsync(state => state.AccountId == fixture.Account.AccountId, Token)).Revision);
    }

    [Fact]
    public async Task Trusted_revocation_and_expired_grants_cannot_be_hidden_by_a_cache_hit()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var signer = fixture.Relay.AddPeer(peer);
        var grant = await GrantAsync(fixture, peer, signer);
        var manager = fixture.Client.DeviceManager;
        Assert.NotNull(await manager.GetDeviceStateAsync(grant, Token));
        var revoked = fixture.Relay.Devices[peer.AccountId] with { Revision = 1, Certificates = [] };
        revoked = revoked with { AccountSignature = [.. peer.Sign(revoked.GetSigningInput(TestNetwork.Context))] };
        fixture.Relay.Devices[peer.AccountId] = revoked;
        await Assert.ThrowsAsync<CryptographicException>(() => manager.RefreshDeviceStateAsync(grant, Token));
        await Assert.ThrowsAsync<CryptographicException>(() => manager.GetDeviceStateAsync(grant, Token));
        await using var database = fixture.Database.Open();
        Assert.Equal(1, (await database.DeviceStates.SingleAsync(state => state.AccountId == peer.AccountId, Token)).Revision);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.GetDeviceStateAsync(grant, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_state_is_observed_without_cache_callbacks_and_survives_a_late_response(bool revoke)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var signer = fixture.Relay.AddPeer(peer);
        var manager = fixture.Client.DeviceManager;
        var previous = (await manager.GetDeviceStateAsync(peer.AccountId, Token))!;
        var count = Queries(fixture);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfflineRelay.Json(previous);
            }
            return fixture.Relay.Respond(request);
        };
        var pending = manager.RefreshDeviceStateAsync(peer.AccountId, Token);
        var updated = previous with { Revision = previous.Revision + 1, Certificates = revoke ? [] : previous.Certificates };
        updated = updated with { AccountSignature = [.. peer.Sign(updated.GetSigningInput(TestNetwork.Context))] };
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using (var database = fixture.Database.Open())
            {
                var row = await database.DeviceStates.SingleAsync(value => value.AccountId == peer.AccountId, Token);
                row.Revision = updated.Revision;
                row.DocumentJson = updated.ToJson();
                await database.SaveChangesAsync(Token);
            }
            var current = (await manager.GetDeviceStateAsync(peer.AccountId, Token).WaitAsync(TimeSpan.FromSeconds(5), Token))!;
            Assert.Equal(updated.ToJson(), current.ToJson());
            Assert.Equal(revoke, current.ValidateDeviceAuthorization(signer.Certificate.GetDeviceId(TestNetwork.Context), TestNetwork.Context) is not null);
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        Assert.Equal(updated.Revision, (await manager.GetDeviceStateAsync(peer.AccountId, Token))!.Revision);
        Assert.Equal(count + 1, Queries(fixture));
    }

    [Fact]
    public async Task Version_notification_keeps_snapshot_reads_available_and_explicit_refresh_checks_the_revision()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.DeviceManager;
        var previous = manager.DeviceState!;
        var connected = AsyncTest.Signal();
        var observed = AsyncTest.Signal();
        var relay = await fixture.Pool.GetAsync(fixture.Relay.RelayId, manager, Token);
        relay.SocketConnected += (_, _) => connected.TrySetResult();
        await manager.StartAsync(Token);
        relay.NotificationReceived += (_, request) => { if (request.Method == "device.state.changed") observed.TrySetResult(); };
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var count = Queries(fixture);
        var newer = previous with { Revision = previous.Revision + 1 };
        newer = newer with { AccountSignature = [.. fixture.Account.Sign(newer.GetSigningInput(TestNetwork.Context))] };
        fixture.Relay.Devices[fixture.Account.AccountId] = newer;
        fixture.Relay.Sockets.Last().Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "device.state.changed", @params = new { revision = newer.Revision } }));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(previous.Revision, (await manager.GetDeviceStateAsync(cancellationToken: Token))!.Revision);
        Assert.Equal(count, Queries(fixture));
        Assert.Equal(newer.Revision, (await manager.RefreshDeviceStateAsync(cancellationToken: Token))!.Revision);
        Assert.Equal(count + 1, Queries(fixture));
        fixture.Relay.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(count + 1, Queries(fixture));
        await manager.StopAsync(Token);
    }

    [Fact]
    public async Task Administrative_read_waits_for_older_lookup_then_fetches_current_state()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.DeviceManager;
        var previous = manager.DeviceState!;
        var newer = previous with { Revision = previous.Revision + 1 };
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
        var old = manager.RefreshDeviceStateAsync(cancellationToken: Token);
        Task<AccountDeviceState?> current;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            fixture.Relay.Devices[fixture.Account.AccountId] = newer;
            current = manager.GetOwnDeviceStateAsync(fixture.Relay.RelayId, Token);
            Assert.False(current.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await old;
        Assert.Equal(newer.Revision, (await current)!.Revision);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task Expired_certificate_cannot_authorize_operations_from_an_unexpired_state_snapshot()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.DeviceManager;
        await manager.RenewDeviceAsync(TimeSpan.FromSeconds(30), Token);
        await manager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(30));
        var count = Queries(fixture);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId,
                new() { Body = new() { ContentType = "text/plain", Text = "expired device" } }, Token));
        Assert.Equal(count + 1, Queries(fixture));
    }

    [Fact]
    public async Task Persisted_state_is_cryptographically_verified_before_reuse()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var invalid = fixture.Client.DeviceState! with { AccountSignature = [.. new byte[64]] };
        await using (var database = fixture.Database.Open())
            await database.DeviceStates.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DocumentJson, invalid.ToJson()), Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReopenAsync());
    }

    static async Task<ContactGrant> GrantAsync(TestClient fixture, AccountSigner account, DeviceSigner signer)
    {
        var grant = new ContactGrant
        {
            Grantor = account.AccountId,
            Grantee = fixture.Account.AccountId,
            ExpiresAt = Clock.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
        };
        return grant with { Signatures = grant.Signatures.Add(signer.Certificate.GetDeviceId(TestNetwork.Context), [.. await signer.SignAsync(grant.GetSigningInput(TestNetwork.Context), Token)]) };
    }
}
