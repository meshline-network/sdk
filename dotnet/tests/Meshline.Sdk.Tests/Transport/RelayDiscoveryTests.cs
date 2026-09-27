using Meshline.Tests.Support;

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
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: Token);
        var descriptors = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetDescriptorAsync()));

        Assert.All(descriptors, item => Assert.Equal(relay.RelayId, item.RelayId));
        Assert.Equal(1, relay.RegistryReads);

        relay.Clock.Advance(TimeSpan.FromDays(6));
        relay.Descriptor = relay.SignDescriptor(relay.Descriptor with { ExpiresAt = relay.Clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds() });
        await client.GetDescriptorAsync(cancellationToken: Token);

        Assert.Equal(2, relay.RegistryReads);
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
