using Meshline.Interactions;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Security.Cryptography;

namespace Meshline.Tests.Transport;

public sealed class DetachedAuthenticationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Authentication_observer_failure_survives_canceled_waiters_and_successful_retry(bool renewal, bool cancelCaller, bool cancellationError)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var pool = relay.Pool(account);
        var release = AsyncTest.Signal();
        try
        {
            relay.SessionSeconds = 30;
            relay.Handler = (request, _) => Task.FromResult(request.Method == "probe" ? new HttpResponseMessage(System.Net.HttpStatusCode.NoContent) : relay.Respond(request));
            var signer = new FailingSigner(new DeviceSigner(account, clock), release.Task, renewal ? 2 : 1);
            var client = renewal ? await pool.GetAsync(relay.RelayId, signer, Token) : await pool.GetAsync(relay.RelayId, Token);
            var notified = AsyncTest.Signal();
            Exception observerError = cancellationError ? new OperationCanceledException("authentication observer failed") : new IOException("authentication observer failed");
            var seen = 0;
            client.StateChanged += (_, _) =>
            {
                if (client.LastError is not CryptographicException { Message: "signer unavailable" }
                    || Interlocked.Exchange(ref seen, 1) != 0) return;
                notified.TrySetResult();
                throw observerError;
            };
            if (renewal) clock.Advance(TimeSpan.FromSeconds(25));
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Task Request(CancellationToken token) => renewal ? client.SendHttpAsync(HttpMethod.Post, "probe", cancellationToken: token) : pool.GetAsync(relay.RelayId, signer, token);
            var pending = Request(caller.Token);
            await signer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (cancelCaller)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
            }
            release.TrySetResult();
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (!cancelCaller) Assert.Same(observerError, await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Token)));
            // Retry can briefly join the completing attempt. Once it succeeds, the
            // abandoned failure must still be retained separately from the new task.
            var retryObserved = false;
            if (cancelCaller)
                await AsyncTest.UntilAsync(async () =>
                {
                    var failure = await Record.ExceptionAsync(() => Request(Token));
                    if (failure is null) return true;
                    Assert.Same(observerError, failure);
                    retryObserved = true;
                    return false;
                });
            var error = await Record.ExceptionAsync(() => pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
            if (cancelCaller && !retryObserved) Assert.Same(observerError, error);
            else Assert.Null(error);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_canceled_waiter_does_not_cancel_authentication_or_replay_a_failure_delivered_to_another(bool renewal)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var release = AsyncTest.Signal();
        var signer = new FailingSigner(new DeviceSigner(account, clock), release.Task, renewal ? 2 : 1);
        relay.SessionSeconds = 30;
        var client = renewal ? await pool.GetAsync(relay.RelayId, signer, Token) : await pool.GetAsync(relay.RelayId, Token);
        var observerError = new IOException("authentication observer failed");
        var seen = 0;
        client.StateChanged += (_, _) =>
        {
            if (client.LastError is CryptographicException { Message: "signer unavailable" } && Interlocked.Exchange(ref seen, 1) == 0) throw observerError;
        };
        if (renewal) clock.Advance(TimeSpan.FromSeconds(25));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task Request(CancellationToken token) => renewal ? client.SendHttpAsync(HttpMethod.Post, "probe", cancellationToken: token) : pool.GetAsync(relay.RelayId, signer, token);
        var canceled = Request(caller.Token);
        try
        {
            await signer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var active = Request(Token);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            release.TrySetResult();
            Assert.Same(observerError, await Record.ExceptionAsync(() => active.WaitAsync(TimeSpan.FromSeconds(5), Token)));
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_authentication_failure_after_caller_cancellation_does_not_fail_disposal(bool renewal)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var release = AsyncTest.Signal();
        var signer = new FailingSigner(new DeviceSigner(account, clock), release.Task, renewal ? 2 : 1);
        relay.SessionSeconds = 30;
        var client = renewal ? await pool.GetAsync(relay.RelayId, signer, Token) : await pool.GetAsync(relay.RelayId, Token);
        var failed = AsyncTest.Signal();
        client.StateChanged += (_, _) => { if (client.LastError is CryptographicException { Message: "signer unavailable" }) failed.TrySetResult(); };
        if (renewal) clock.Advance(TimeSpan.FromSeconds(25));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task pending = renewal ? client.SendHttpAsync(HttpMethod.Post, "probe", cancellationToken: caller.Token) : pool.GetAsync(relay.RelayId, signer, caller.Token);
        try
        {
            await signer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            release.TrySetResult();
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        finally { release.TrySetResult(); }
    }

    sealed class FailingSigner(IDeviceSigner inner, Task release, int failAt) : IDeviceSigner
    {
        int calls;
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public DeviceCertificate Certificate => inner.Certificate;
        public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == failAt)
            {
                Entered.TrySetResult();
                await release.WaitAsync(cancellationToken);
                throw new CryptographicException("signer unavailable");
            }
            return await inner.SignAsync(data, cancellationToken);
        }
    }
}
