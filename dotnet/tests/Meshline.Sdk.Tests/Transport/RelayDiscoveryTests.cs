using Meshline.Tests.Support;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Registry;
using Meshline.Transport;
using System.Runtime.CompilerServices;

namespace Meshline.Tests.Transport;

public sealed class RelayDiscoveryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovery_is_coalesced_cached_and_refreshed_after_expiry()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var registry = new CachedRelayRegistry(relay);
        await using var pool = new RelayClientPool(relay.Options(account), registry, relay.Http);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: Token);
        var descriptors = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetDescriptorAsync()));

        Assert.All(descriptors, item => Assert.Equal(relay.RelayId, item.RelayId));
        Assert.Equal(1, relay.RegistryReads);

        relay.Clock.Advance(TimeSpan.FromDays(6));
        relay.Descriptor = relay.SignDescriptor(relay.Descriptor with { ExpiresAt = relay.Clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds() });
        await client.GetDescriptorAsync(cancellationToken: Token);

        await AsyncTest.UntilAsync(() => relay.RegistryReads == 2);
    }

    [Fact]
    public async Task Late_directory_results_cannot_resurrect_a_removed_relay()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var source = new RegistrySource(relay.Entry)
        {
            BeforeSnapshot = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            Read = _ => Task.FromResult<RelayEntry?>(null)
        };
        await using var registry = new CachedRelayRegistry(source);
        await using var snapshot = registry.GetRelaysAsync(Token).GetAsyncEnumerator(Token);
        var pending = snapshot.MoveNextAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            // A later lookup observes removal while the older directory still contains the relay.
            Assert.Null(await registry.GetRelayAsync(relay.RelayId, Token));
        }
        finally { release.TrySetResult(); }
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Null(await registry.GetRelayAsync(relay.RelayId, Token));
    }

    [Fact]
    public async Task Descriptor_failure_keeps_unexpired_data_but_never_extends_its_signed_lifetime()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: Token);
        var original = await client.GetDescriptorAsync(Token);
        var failed = AsyncTest.Signal();
        client.ErrorOccurred += (_, _) => failed.TrySetResult();
        var queries = relay.Requests.Count(request => request.Method == "relay.descriptor");
        relay.Handler = (request, _) => request.Method == "relay.descriptor" ? throw new HttpRequestException("offline") : Task.FromResult(relay.Respond(request));
        relay.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Same(original, await client.GetDescriptorAsync(Token));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        relay.Clock.Advance(TimeSpan.FromSeconds(59));
        for (var i = 0; i < 10; i++)
            Assert.Same(original, await client.GetDescriptorAsync(Token));
        Assert.Equal(queries + 1, relay.Requests.Count(request => request.Method == "relay.descriptor"));
        relay.Clock.Advance(TimeSpan.FromDays(7));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetDescriptorAsync(Token));
    }

    [Fact]
    public async Task Registry_status_changes_with_equal_timestamps_take_effect()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        var source = new RegistrySource(relay.Entry);
        await using var registry = new CachedRelayRegistry(source);
        await registry.GetRelayAsync(relay.RelayId, Token);
        source.Read = _ => Task.FromResult<RelayEntry?>(relay.Entry with { Status = RelayStatus.Disabled });
        relay.Clock.Advance(TimeSpan.FromHours(1));
        await registry.GetRelayAsync(relay.RelayId, Token);
        await AsyncTest.UntilAsync(async () => (await registry.GetRelayAsync(relay.RelayId, Token))?.Status == RelayStatus.Disabled);
    }

    [Fact]
    public async Task Limits_reuse_results_while_server_time_queries_remain_live()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: Token);
        for (var i = 0; i < 8; i++)
            await client.GetLimitsAsync(Token);
        Assert.Single(relay.Requests, request => request.Method == "relay.info");
        await client.GetInfoAsync(Token);
        Assert.Equal(2, relay.Requests.Count(request => request.Method == "relay.info"));
    }

    [Fact]
    public async Task Descriptor_reads_can_observe_a_later_registry_reactivation()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var registry = new CachedRelayRegistry(relay);
        await using var pool = new RelayClientPool(relay.Options(account), registry, relay.Http);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: Token);
        await client.GetDescriptorAsync(Token);
        relay.Entry = relay.Entry with { Status = RelayStatus.Disabled, UpdatedAt = 2 };
        relay.Clock.Advance(TimeSpan.FromHours(1));
        await registry.GetRelayAsync(relay.RelayId, Token);
        await AsyncTest.UntilAsync(async () => (await registry.GetRelayAsync(relay.RelayId, Token))?.Status == RelayStatus.Disabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDescriptorAsync(Token));
        relay.Entry = relay.Entry with { Status = RelayStatus.Active, UpdatedAt = 3 };
        relay.Clock.Advance(TimeSpan.FromHours(1));
        // The registry refresh may finish before or after this descriptor read.
        var refreshError = await Record.ExceptionAsync(() => client.GetDescriptorAsync(Token));
        Assert.True(refreshError is null or InvalidOperationException);
        await AsyncTest.UntilAsync(async () => (await registry.GetRelayAsync(relay.RelayId, Token))?.Status == RelayStatus.Active);
        Assert.Equal(relay.RelayId, (await client.GetDescriptorAsync(Token)).RelayId);
    }

    [Theory]
    [InlineData(RelayStatus.Disabled)]
    [InlineData(RelayStatus.Suspended)]
    [InlineData(null)]
    public async Task Direct_registry_changes_take_effect_without_expiring_the_descriptor(RelayStatus? status)
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var source = new RegistrySource(relay.Entry);
        await using var pool = new RelayClientPool(relay.Options(account), source, relay.Http);
        Assert.Same(source, pool.Registry);
        var client = await pool.GetAsync(relay.RelayId, Token);
        var descriptor = await client.GetDescriptorAsync(Token);
        source.Read = _ => Task.FromResult(status is { } value ? relay.Entry with { Status = value } : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendHttpAsync(HttpMethod.Get, "probe", authenticated: false, cancellationToken: Token));
        Assert.DoesNotContain(relay.Requests, request => request.Method == "probe");
        source.Read = _ => Task.FromResult<RelayEntry?>(relay.Entry);
        Assert.Same(descriptor, await client.GetDescriptorAsync(Token));
        Assert.Single(relay.Requests, request => request.Method == "relay.descriptor");
        await pool.DisposeAsync();
        Assert.Equal(0, source.Disposals);
        Assert.Equal(relay.Entry, await source.GetRelayAsync(relay.RelayId, Token));
    }

    [Fact]
    public async Task Descriptor_request_rechecks_registration_after_network_completion()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        relay.Handler = async (request, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return relay.Respond(request);
        };
        var pending = client.GetDescriptorAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            relay.Entry = relay.Entry with { Status = RelayStatus.Disabled };
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
    }

    [Fact]
    public async Task Optional_registry_is_shared_across_pools_and_owns_only_its_refreshes()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var source = new RegistrySource(relay.Entry);
        await using var registry = new CachedRelayRegistry(source);
        await using var first = new RelayClientPool(relay.Options(account), registry, relay.Http);
        await using var second = new RelayClientPool(relay.Options(account), registry, relay.Http);
        Assert.Same(registry, first.Registry);
        Assert.Same(registry, second.Registry);
        var a = await first.GetAsync(relay.RelayId, Token);
        var b = await second.GetAsync(relay.RelayId, Token);
        await Task.WhenAll(a.GetDescriptorAsync(Token), b.GetDescriptorAsync(Token));
        Assert.Equal(1, source.Reads);
        await first.DisposeAsync();
        await b.GetDescriptorAsync(Token);
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var cleanup = AsyncTest.Signal();
        source.Read = async token =>
        {
            entered.TrySetResult();
            await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, cleanup.Task);
            return relay.Entry;
        };
        Task disposal;
        try
        {
            relay.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(1, source.Reads); // No timer runs when the hour elapses.
            var reads = Enumerable.Range(0, 12).Select(_ => registry.GetRelayAsync(relay.RelayId, Token));
            Assert.All(await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5), Token), entry => Assert.Equal(relay.Entry, entry));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(2, source.Reads);
            await second.DisposeAsync();
            Assert.False(canceled.Task.IsCompleted);
            disposal = registry.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(disposal.IsCompleted);
        }
        finally { cleanup.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(0, source.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.GetRelayAsync(relay.RelayId, Token));
    }

    [Fact]
    public async Task Optional_registry_coalesces_cold_reads_isolates_cancellation_and_backs_off_failures()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var source = new RegistrySource(relay.Entry)
        {
            Read = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                throw new HttpRequestException("offline");
            }
        };
        await using var registry = new CachedRelayRegistry(source);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var canceled = registry.GetRelayAsync(relay.RelayId, caller.Token);
        var shared = registry.GetRelayAsync(relay.RelayId, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.False(shared.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<HttpRequestException>(() => shared);
        relay.Clock.Advance(TimeSpan.FromSeconds(59));
        await Assert.ThrowsAsync<HttpRequestException>(() => registry.GetRelayAsync(relay.RelayId, Token));
        Assert.Equal(1, source.Reads);
        source.Read = _ => Task.FromResult<RelayEntry?>(relay.Entry);
        relay.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, source.Reads); // Elapsed time alone does not start a retry.
        Assert.Equal(relay.Entry, await registry.GetRelayAsync(relay.RelayId, Token));
        Assert.Equal(2, source.Reads);
    }

    sealed class RegistrySource(RelayEntry entry) : IRelayRegistry, IAsyncDisposable
    {
        public NetworkContext Context => TestNetwork.Context;
        public int Reads;
        public int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        public RelayEntry[] Snapshot = [entry];
        public Func<CancellationToken, Task> BeforeSnapshot = _ => Task.CompletedTask;
        public Func<CancellationToken, Task<RelayEntry?>> Read = _ => Task.FromResult<RelayEntry?>(entry);
        public Task<RelayEntry?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return Read(cancellationToken);
        }
        public async IAsyncEnumerable<RelayEntry> GetRelaysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var snapshot = Snapshot;
            await BeforeSnapshot(cancellationToken);
            foreach (var value in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
        }
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("signature")]
    [InlineData("expiry")]
    public async Task Invalid_discovery_does_not_reach_business_requests(string defect)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        if (defect == "endpoint")
            relay.Entry = relay.Entry with
            {
                Endpoint = "http://relay.test"
            };
        if (defect == "signature")
            relay.Descriptor = relay.Descriptor with
            {
                RelaySignature = [.. new byte[64]]
            };
        if (defect == "expiry")
            relay.Descriptor = relay.SignDescriptor(relay.Descriptor with { ExpiresAt = relay.Clock.GetUtcNow().ToUnixTimeSeconds() });
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendHttpAsync(HttpMethod.Get, "probe", authenticated: false, cancellationToken: Token));
        Assert.DoesNotContain(relay.Requests, request => request.Method == "probe");
    }
}
