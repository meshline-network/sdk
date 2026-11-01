using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class StateUpdateLifecycleTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Canceling_the_startup_token_after_start_does_not_cancel_pending_retries()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await manager.StartAsync(startup.Token);
        await startup.CancelAsync();
        fixture.Relay.Handler = null;
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(() => manager.Profile?.Nickname == "latest");
        await manager.StopAsync(Token);

        await using var database = fixture.Database.Open();
        Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);
    }

    [Fact]
    public async Task Stopping_preserves_the_pending_retry_and_starting_resumes_it()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var manager = fixture.Client.ProfileManager;
        fixture.Relay.Handler = (request, _) => request.Method == "profile.publish"
            ? throw new TimeoutException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => manager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        await manager.StopAsync(Token);

        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "profile.publish");
        await using (var database = fixture.Database.Open())
            Assert.True((await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);

        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => manager.Profile?.Nickname == "latest");
        await manager.StopAsync(Token);
        Assert.Single(fixture.Relay.Requests, value => value.Method == "profile.publish");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_drains_superseded_signing_without_a_running_runtime(bool startAndStop)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var signer = new PausedDeviceSigner(fixture.Client.DeviceManager);
        await using var manager = new ProfileManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Client.AccountManager, signer);
        await manager.InitializeAsync(Token);
        if (startAndStop) await manager.StartAsync(Token);
        var old = manager.UpdateProfileAsync(new() { Nickname = "old", Bio = "retained" }, Token);
        try
        {
            await signer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await manager.UpdateProfileAsync(new() { Nickname = "latest" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => old);
            Assert.Equal("retained", latest.Bio);
            if (startAndStop) await manager.StopAsync(Token);

            var disposal = manager.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            signer.Release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);

            Assert.Equal(ComponentState.Disposed, manager.LifecycleState);
            Assert.Equal(latest.ToJson(), manager.Profile!.ToJson());
            await using var database = fixture.Database.Open();
            var publication = await database.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token);
            Assert.False(publication.Pending);
            Assert.Equal(latest.ToJson(), publication.DocumentJson);
        }
        finally { signer.Release.TrySetResult(); }
    }

    sealed class PausedDeviceSigner(IDeviceSigner inner) : IDeviceSigner
    {
        int calls;
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();
        public DeviceCertificate Certificate => inner.Certificate;

        public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Entered.TrySetResult();
                await Release.Task; // External signers can keep working after supersession or disposal.
            }
            return await inner.SignAsync(data, CancellationToken.None);
        }
    }
}
