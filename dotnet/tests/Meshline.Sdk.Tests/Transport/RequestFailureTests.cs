using Meshline.Interactions;
using Meshline.Models;
using Meshline.Tests.Support;
using System.Net;
using System.Net.Http.Headers;

namespace Meshline.Tests.Transport;

public sealed class RequestFailureTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "timeout")]
    [InlineData(false, "caller")]
    [InlineData(false, "dependency")]
    [InlineData(false, "network")]
    [InlineData(true, "timeout")]
    [InlineData(true, "caller")]
    [InlineData(true, "dependency")]
    [InlineData(true, "network")]
    public async Task Response_body_failures_preserve_their_source(bool registry, string cause)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, cancellationToken: Token);
        await client.GetDescriptorAsync(Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        Exception original = cause == "network" ? new HttpRequestException("Connection lost during body read.") : new OperationCanceledException("Dependency stopped.");
        var content = new PendingContent(async ct =>
        {
            entered.TrySetResult();
            if (cause is "dependency" or "network") { await release.Task.WaitAsync(ct); throw original; }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        Task<HttpResponseMessage> Respond() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        relay.Handler = (_, _) => Respond();
        using var http = new HttpClient(new Handler(Respond));
        var directory = new RpcRelayRegistry(http, new()
        {
            Context = new NetworkContext { Reference = 12345, Registry = "0x0123456789012345678901234567890123456789" },
            RpcUrl = new Uri("https://neo.test"),
            RequestTimeout = TimeSpan.FromSeconds(60)
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task pending = registry ? directory.GetRelayAsync(relay.RelayId, caller.Token)
            : client.SendHttpAsync(HttpMethod.Get, "probe", authenticated: false, cancellationToken: caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (cause == "caller") caller.Cancel();
        else if (cause == "timeout") clock.Advance(TimeSpan.FromSeconds(60));
        else release.TrySetResult();

        if (cause == "timeout")
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal(registry ? "registry.getversion" : "relay.http.probe", error.Data["operation"]);
            Assert.Equal(60d, error.Data["timeoutSeconds"]);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        }
        else if (cause == "caller") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.Same(original, await Record.ExceptionAsync(() => pending));
    }

    sealed class Handler(Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond();
    }

    sealed class PendingContent : HttpContent
    {
        readonly Func<CancellationToken, Task> read;
        public PendingContent(Func<CancellationToken, Task> read)
        {
            this.read = read;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => read(CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => read(cancellationToken);
    }
}
