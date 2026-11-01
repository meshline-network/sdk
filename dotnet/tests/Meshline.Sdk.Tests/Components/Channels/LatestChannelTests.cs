using Meshline.Components;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Channels;

public sealed class LatestChannelTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_refresh_failure_cannot_impose_a_cooldown_after_a_successful_update(bool transportFailure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new ChannelRelay(fixture);
        host.Install();
        var channel = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "initial", cancellationToken: Token);
        await fixture.ReopenAsync();
        var manager = fixture.Client.ChannelManager;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var reads = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method != "channel.resolve" || Interlocked.Increment(ref reads) != 1) return host.Respond(request);
            var response = host.Respond(request);
            entered.TrySetResult();
            await release.Task;
            if (!transportFailure) return response;
            response.Dispose();
            throw new HttpRequestException("old refresh failed");
        };
        var old = manager.RefreshChannelAsync(channel.Ref, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await manager.UpdateChannelAsync(channel.Ref, new() { Name = "latest" }, Token);
            release.TrySetResult();
            await Assert.ThrowsAsync(transportFailure ? typeof(HttpRequestException) : typeof(InvalidDataException), () => old);
            var refreshed = await manager.RefreshChannelAsync(channel.Ref, Token);
            Assert.Equal(latest.Descriptor!.ToJson(), refreshed.Descriptor!.ToJson());
            Assert.Equal(3, Volatile.Read(ref reads));
            await using var database = fixture.Database.Open();
            Assert.Equal(latest.Descriptor.ToJson(), (await database.Channels.SingleAsync(Token)).DescriptorJson);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => old);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Descriptor_updates_do_not_wait_for_missing_post_history_and_late_history_preserves_newer_state(bool deleteDuringRead)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new ChannelRelay(fixture);
        host.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "initial", cancellationToken: Token);
        var post = await manager.PublishPostAsync(channel.Ref, new() { Body = new() { ContentType = "text/plain", Text = "original" } }, Token);
        await manager.EditPostAsync(post.Ref, new() { Body = new(new() { ContentType = "text/plain", Text = "intermediate" }) }, Token);
        await manager.EditPostAsync(post.Ref, new() { Body = new(new() { ContentType = "text/plain", Text = "edited" }) }, Token);
        await using (var database = fixture.Database.Open()) await database.ChannelPosts.ExecuteDeleteAsync(Token);
        host.TransformPage = page => page.Events.Length > 1 ? page with { Events = [page.Events[0]], HasMore = true } : page;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var reads = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            var response = host.Respond(request);
            if (request.Method == "channel.read" && Interlocked.Increment(ref reads) == 2)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return response;
        };
        var history = manager.LoadChannelHistoryAsync(channel.Ref, new() { Limit = 1 }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await manager.UpdateChannelAsync(channel.Ref, new() { Name = "latest" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (deleteDuringRead) await manager.DeletePostAsync(post.Ref, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            release.TrySetResult();
            await history.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using var database = fixture.Database.Open();
            Assert.Equal(latest.Descriptor!.ToJson(), (await database.Channels.SingleAsync(Token)).DescriptorJson);
            Assert.False((await database.ChannelOperations.SingleAsync(value => value.Method == "channel.update", Token)).Pending);
            var stored = await database.ChannelPosts.SingleAsync(Token);
            Assert.Equal(deleteDuringRead, stored.IsDeleted);
            if (deleteDuringRead) Assert.Null(stored.PostJson);
            else Assert.Equal("edited", ProtocolModel.FromJson<ChannelPost>(stored.PostJson!)!.Body!.Text);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => history);
        }
    }

    [Fact]
    public async Task An_owned_channel_can_be_updated_without_a_local_descriptor_cache()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new ChannelRelay(fixture);
        host.Install();
        var channel = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "initial", cancellationToken: Token);
        await fixture.Client.DisposeAsync();
        await using (var database = fixture.Database.Open())
        {
            await database.ChannelDescriptors.ExecuteDeleteAsync(Token);
            await database.Channels.ExecuteDeleteAsync(Token);
        }
        await fixture.ReopenAsync();
        var result = await fixture.Client.ChannelManager.UpdateChannelAsync(channel.Ref, new() { Name = "updated" }, Token);
        Assert.Equal("updated", result.Descriptor!.Name);
        Assert.Equal(1, result.Descriptor.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_updates_merge_and_old_requests_cannot_replace_the_latest_descriptor(bool acceptBeforeDelay)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new ChannelRelay(fixture);
        host.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "initial", cancellationToken: Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var processed = AsyncTest.Signal();
        var writes = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method != "channel.update" || Interlocked.Increment(ref writes) != 1) return host.Respond(request);
            var response = acceptBeforeDelay ? host.Respond(request) : null;
            entered.TrySetResult();
            await release.Task;
            try { return response ?? host.Respond(request); }
            finally { processed.TrySetResult(); }
        };
        var first = manager.UpdateChannelAsync(channel.Ref, new() { Name = "renamed" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await manager.UpdateChannelAsync(channel.Ref, new() { Description = "latest description" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => first);
            Assert.Equal("renamed", latest.Descriptor!.Name);
            Assert.Equal("latest description", latest.Descriptor.Description);
            release.TrySetResult();
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(latest.Descriptor.ToJson(), host.Descriptors.Values.MaxBy(value => value.Revision)!.ToJson());
            await using var database = fixture.Database.Open();
            Assert.False((await database.ChannelOperations.SingleAsync(value => value.Method == "channel.update", Token)).Pending);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Timeout_replacement_and_restart_keep_only_the_latest_channel_patch()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new ChannelRelay(fixture);
        host.Install();
        var channel = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "initial", cancellationToken: Token);
        fixture.Relay.Handler = (request, _) => request.Method == "channel.update"
            ? throw new TimeoutException("offline") : Task.FromResult(host.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ChannelManager.UpdateChannelAsync(channel.Ref, new() { Name = "old", Description = "retained" }, Token));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.ChannelManager.UpdateChannelAsync(channel.Ref, new() { Name = "latest" }, Token));
        await fixture.ReopenAsync();
        host.Install();
        var updated = await fixture.Client.ChannelManager.UpdateChannelAsync(channel.Ref, new(), Token);
        Assert.Equal("latest", updated.Descriptor!.Name);
        Assert.Equal("retained", updated.Descriptor.Description);
        Assert.Equal(2, host.Descriptors.Count);
    }

    [Fact]
    public async Task A_conflict_after_preparation_rebuilds_the_latest_patch_against_the_current_revision()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new ChannelRelay(fixture);
        host.Install();
        var channel = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "initial", cancellationToken: Token);
        var first = true;
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "channel.update" && first)
            {
                first = false;
                var competing = host.Descriptors[0] with { Revision = 1, Description = "external", DeviceSignature = [] };
                competing = competing with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(competing.GetSigningInput(TestNetwork.Context), token)] };
                host.Descriptors[1] = competing;
                return OfflineRelay.Error("state_conflict");
            }
            return host.Respond(request);
        };
        var result = await fixture.Client.ChannelManager.UpdateChannelAsync(channel.Ref, new() { Name = "latest" }, Token);
        Assert.Equal(2, result.Descriptor!.Revision);
        Assert.Equal("latest", result.Descriptor.Name);
        Assert.Equal("external", result.Descriptor.Description);
    }
}
