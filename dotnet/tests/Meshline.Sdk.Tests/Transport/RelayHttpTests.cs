using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using System.Net;

namespace Meshline.Tests.Transport;

public sealed class RelayHttpTests
{
    [Theory]
    [InlineData(302, "application/json", "{}", typeof(HttpRequestException))]
    [InlineData(200, "text/html", "{}", typeof(InvalidDataException))]
    [InlineData(201, "application/json", "{}", typeof(InvalidDataException))]
    [InlineData(204, "application/json", "", typeof(InvalidDataException))]
    [InlineData(200, "application/json", "{broken", typeof(System.Text.Json.JsonException))]
    public async Task Invalid_http_responses_are_not_accepted(int status, string type, string body, Type exception)
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: TestContext.Current.CancellationToken);
        await client.GetDescriptorAsync(cancellationToken: TestContext.Current.CancellationToken);
        relay.Handler = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, type) });

        await Assert.ThrowsAsync(
            exception,
            () => client.SendHttpAsync<SequenceResult>(HttpMethod.Get, "probe", authenticated: false, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Request_timeout_and_caller_cancellation_do_not_use_wall_clock_waits()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: TestContext.Current.CancellationToken);
        await client.GetDescriptorAsync(cancellationToken: TestContext.Current.CancellationToken);
        var entered = AsyncTest.Signal();
        relay.Handler = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.NoContent);
        };
        var request = client.SendHttpAsync(HttpMethod.Get, "probe", authenticated: false, cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        relay.Clock.Advance(TimeSpan.FromSeconds(60));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("relay.http.probe", error.Data["operation"]);
        Assert.Equal(60d, error.Data["timeoutSeconds"]);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);

        using var cancellation = new CancellationTokenSource();
        var second = client.SendHttpAsync(HttpMethod.Get, "probe", authenticated: false, cancellationToken: cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task Rate_limit_blocks_following_requests_until_retry_after()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: TestContext.Current.CancellationToken);
        await client.GetDescriptorAsync(cancellationToken: TestContext.Current.CancellationToken);
        var calls = 0;
        relay.Handler = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var response = OfflineRelay.Json("{\"code\":\"rate_limited\",\"message\":\"wait\",\"data\":{\"retry_after\":10}}", HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "10");
            return Task.FromResult(response);
        };

        await Assert.ThrowsAsync<RelayException>(() => client.SendHttpAsync(HttpMethod.Get, "probe", authenticated: false, cancellationToken: TestContext.Current.CancellationToken));

        var next = client.SendHttpAsync(HttpMethod.Get, "probe", authenticated: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(next.IsCompleted);
        Assert.Equal(1, calls);

        relay.Clock.Advance(TimeSpan.FromSeconds(10));
        await next.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }
}
