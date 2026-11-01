using Meshline.Components;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Components;

public sealed class LatestStateUpdateTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Profile_and_removal_successors_share_route_discovery_and_publish_only_latest_state(bool removeDevice, bool timeout)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.DisposeAsync();
        await using (var database = fixture.Database.Open()) await database.AccountRoutes.ExecuteDeleteAsync(Token);
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var reads = 0;
        CancellationToken queryToken = default;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "account.route.resolve")
            {
                Interlocked.Increment(ref reads);
                queryToken = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var method = removeDevice ? "device.state.publish" : "profile.publish";
        Task first = removeDevice
            ? fixture.Client.DeviceManager.RemoveDeviceAsync(deviceId, Token)
            : fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "old", Bio = "retained" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using (var admission = fixture.Database.Open())
            {
                var admitted = await admission.SignedRequests.SingleAsync(value => value.Method == method, Token);
                Assert.True(admitted.Pending);
                Assert.NotNull(admitted.IntentJson);
                Assert.Equal(PublicationRequestState.None, admitted.RequestState);
            }
            Task second = removeDevice
                ? fixture.Client.DeviceManager.RemoveDeviceAsync(deviceId, Token)
                : fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "intermediate" }, Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => first.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Task latest = removeDevice
                ? fixture.Client.DeviceManager.RemoveDeviceAsync(deviceId, Token)
                : fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => second.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.False(latest.IsCompleted);
            Assert.False(queryToken.IsCancellationRequested);
            Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == method);
            await using (var admission = fixture.Database.Open())
            {
                var admitted = await admission.SignedRequests.SingleAsync(value => value.Method == method, Token);
                Assert.Equal(3, admitted.Generation);
                Assert.True(admitted.Pending);
                Assert.Equal(PublicationRequestState.None, admitted.RequestState);
                var intent = JsonNode.Parse(admitted.IntentJson!)!;
                if (removeDevice) Assert.True(intent["remove:" + deviceId]!.GetValue<bool>());
                else
                {
                    Assert.Equal("latest", intent["nickname"]!.GetValue<string>());
                    Assert.Equal("retained", intent["bio"]!.GetValue<string>());
                }
            }
            if (timeout)
            {
                fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(60));
                var failure = await Assert.ThrowsAsync<HttpRequestException>(() => latest.WaitAsync(TimeSpan.FromSeconds(5), Token));
                var cause = Assert.IsType<TimeoutException>(Assert.Single(Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions));
                Assert.Equal("relay.http.account.route.resolve", cause.Data["operation"]);
                await using (var pending = fixture.Database.Open())
                {
                    var row = await pending.SignedRequests.SingleAsync(value => value.Method == method, Token);
                    Assert.True(row.Pending);
                    Assert.NotNull(row.IntentJson);
                    Assert.Equal(Clock.UtcNow.AddSeconds(1), row.NextAttemptAt);
                }
                Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == method);
                await fixture.ReopenAsync();
                fixture.Relay.Handler = null;
                fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
                var manager = removeDevice ? (ClientComponent)fixture.Client.DeviceManager : fixture.Client.ProfileManager;
                await manager.StartAsync(Token);
                await AsyncTest.UntilAsync(async () =>
                {
                    await using var pending = fixture.Database.Open();
                    return !(await pending.SignedRequests.SingleAsync(value => value.Method == method, Token)).Pending;
                });
                await manager.StopAsync(Token);
            }
            else
            {
                release.TrySetResult();
                await latest.WaitAsync(TimeSpan.FromSeconds(5), Token);
            }
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.Single(fixture.Relay.Requests, value => value.Method == method);
            if (removeDevice) Assert.Empty(fixture.Client.DeviceState!.Certificates);
            else
            {
                Assert.Equal("latest", fixture.Client.ProfileManager.Profile!.Nickname);
                Assert.Equal("retained", fixture.Client.ProfileManager.Profile.Bio);
            }
            await using var database = fixture.Database.Open();
            Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == method, Token)).Pending);
            Assert.NotNull(fixture.Client.AccountManager.Route);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => first);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_recovers_latest_profile_or_removal_admitted_before_route_discovery(bool removeDevice)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.DisposeAsync();
        await using (var database = fixture.Database.Open()) await database.AccountRoutes.ExecuteDeleteAsync(Token);
        await fixture.ReopenAsync();
        fixture.Relay.Handler = (request, _) => request.Method == "account.route.resolve"
            ? throw new TimeoutException("route lookup unavailable") : Task.FromResult(fixture.Relay.Respond(request));
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        await Assert.ThrowsAsync<HttpRequestException>(() => removeDevice
            ? fixture.Client.DeviceManager.RemoveDeviceAsync(deviceId, Token)
            : fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "old", Bio = "retained" }, Token));
        await Assert.ThrowsAsync<HttpRequestException>(() => removeDevice
            ? fixture.Client.DeviceManager.RemoveDeviceAsync(deviceId, Token)
            : fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "latest" }, Token));
        var method = removeDevice ? "device.state.publish" : "profile.publish";
        await using (var database = fixture.Database.Open())
            Assert.Equal(Clock.UtcNow.AddSeconds(1), (await database.SignedRequests.SingleAsync(value => value.Method == method, Token)).NextAttemptAt);
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        var manager = removeDevice ? (ClientComponent)fixture.Client.DeviceManager : fixture.Client.ProfileManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () =>
        {
            await using var database = fixture.Database.Open();
            return !(await database.SignedRequests.SingleAsync(value => value.Method == method, Token)).Pending;
        });
        await manager.StopAsync(Token);
        Assert.Single(fixture.Relay.Requests, value => value.Method == method);
        if (removeDevice) Assert.Empty(fixture.Client.DeviceState!.Certificates);
        else
        {
            Assert.Equal("latest", fixture.Client.ProfileManager.Profile!.Nickname);
            Assert.Equal("retained", fixture.Client.ProfileManager.Profile.Bio);
        }
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 9_007_199_254_740_992)]
    [InlineData(true, 9_007_199_254_740_992)]
    public async Task Invalid_explicit_revision_does_not_supersede_an_inflight_publication(bool device, long revision)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var method = device ? "device.state.publish" : "account.route.publish";
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == method)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        Task first = device
            ? fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token)
            : fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(2), cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using var database = fixture.Database.Open();
            var before = await database.SignedRequests.AsNoTracking().SingleAsync(value => value.Method == method, Token);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device
                ? fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, options: new() { Revision = revision }, cancellationToken: Token)
                : (Task)fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(3), revision, cancellationToken: Token));
            Assert.False(first.IsCompleted);
            var after = await database.SignedRequests.AsNoTracking().SingleAsync(value => value.Method == method, Token);
            Assert.Equal(before.Generation, after.Generation);
            Assert.Equal(before.IntentJson, after.IntentJson);
        }
        finally { release.TrySetResult(); }
        await first;
    }

    [Fact]
    public async Task Duplicate_device_certificates_do_not_supersede_an_inflight_publication()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var manager = fixture.Client.DeviceManager;
        var first = manager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using var database = fixture.Database.Open();
            var before = await database.SignedRequests.AsNoTracking().SingleAsync(value => value.Method == "device.state.publish", Token);
            await Assert.ThrowsAsync<ArgumentException>(() => manager.PublishDeviceStateAsync(fixture.Relay.RelayId,
                [manager.Local!, manager.Local!], cancellationToken: Token));
            Assert.False(first.IsCompleted);
            var after = await database.SignedRequests.AsNoTracking().SingleAsync(value => value.Method == "device.state.publish", Token);
            Assert.Equal(before.Generation, after.Generation);
            Assert.Equal(before.IntentJson, after.IntentJson);
        }
        finally { release.TrySetResult(); }
        await first;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route_and_device_updates_do_not_wait_for_an_old_conflict_read(bool device)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var publish = device ? "device.state.publish" : "account.route.publish";
        var resolve = device ? "device.state.resolve" : "account.route.resolve";
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var writes = 0;
        var reads = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method == publish && Interlocked.Increment(ref writes) == 1) return OfflineRelay.Error("stale_state");
            var response = fixture.Relay.Respond(request);
            if (request.Method == resolve && Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return response;
        };
        Task first = device
            ? fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token)
            : fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(2), cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            string expected;
            if (device)
                expected = (await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!], cancellationToken: Token)
                    .WaitAsync(TimeSpan.FromSeconds(5), Token)).DeviceState.ToJson();
            else
                expected = (await fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(3), cancellationToken: Token)
                    .WaitAsync(TimeSpan.FromSeconds(5), Token)).ToJson();
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => first);
            release.TrySetResult();
            var manager = device ? (ClientComponent)fixture.Client.DeviceManager : fixture.Client.AccountManager;
            await manager.DisposeAsync();
            Assert.Equal(expected, device ? fixture.Client.DeviceState!.ToJson() : fixture.Client.AccountManager.Route!.ToJson());
            await using var database = fixture.Database.Open();
            Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == publish, Token)).Pending);
            Assert.Equal(expected, device ? (await database.DeviceStates.SingleAsync(Token)).DocumentJson : (await database.AccountRoutes.SingleAsync(Token)).DocumentJson);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => first);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Route_and_device_updates_fence_late_requests_and_responses(bool device, bool acceptedBeforeDelay)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var method = device ? "device.state.publish" : "account.route.publish";
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var processed = AsyncTest.Signal();
        var writes = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method != method || Interlocked.Increment(ref writes) != 1) return fixture.Relay.Respond(request);
            var response = acceptedBeforeDelay ? fixture.Relay.Respond(request) : null;
            entered.TrySetResult();
            await release.Task;
            try { return response ?? fixture.Relay.Respond(request); }
            finally { processed.TrySetResult(); }
        };
        Task first = device
            ? fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token)
            : fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(2), cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            string expected;
            if (device)
            {
                await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(7), Token);
                var latest = await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
                expected = latest.DeviceState.ToJson();
            }
            else
            {
                var latest = await fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(3), cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
                expected = latest.ToJson();
            }
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => first);
            release.TrySetResult();
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(expected, device ? fixture.Relay.Devices[fixture.Account.AccountId].ToJson() : fixture.Relay.Routes[fixture.Account.AccountId].ToJson());
            Assert.Equal(expected, device ? fixture.Client.DeviceManager.DeviceState!.ToJson() : fixture.Client.AccountManager.Route!.ToJson());
            await using var database = fixture.Database.Open();
            Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == method, Token)).Pending);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_replays_only_the_latest_route_or_device_state(bool device)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var method = device ? "device.state.publish" : "account.route.publish";
        fixture.Relay.Handler = (request, _) => request.Method == method
            ? throw new TimeoutException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        for (var i = 2; i <= 3; i++)
        {
            if (device)
            {
                await fixture.Client.DeviceManager.RenewDeviceAsync(TimeSpan.FromDays(i), Token);
                await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token));
            }
            else await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(i), cancellationToken: Token));
        }
        var expected = fixture.Relay.Requests.Last(value => value.Method == method).Body;
        await fixture.ReopenAsync();
        fixture.Relay.Handler = null;
        fixture.Relay.Requests.Clear();
        ClientComponent manager = device ? fixture.Client.DeviceManager : fixture.Client.AccountManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(async () =>
        {
            await using var database = fixture.Database.Open();
            return !(await database.SignedRequests.SingleAsync(value => value.Method == method, Token)).Pending;
        });
        await manager.StopAsync(Token);
        Assert.Equal(expected, Assert.Single(fixture.Relay.Requests, value => value.Method == method).Body);
    }
}
