using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Groups;

public sealed class LatestGroupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static GroupCreateOptions Options => new() { Name = "initial", MemberCapacity = 10, InvitePolicy = GroupInvitePolicy.Administrators };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_update_rebases_concurrent_management_but_preserves_writability_checks(bool closed)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new GroupRelay(fixture);
        host.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        using var peer = new AccountSigner();
        string previous;
        await using (var database = fixture.Database.Open()) previous = (await database.Groups.SingleAsync(Token)).ManagementHash!;
        GroupManagementOperation concurrent = closed
            ? new GroupClose { GroupId = group.Ref.GroupId, PrevHash = previous, DeviceSignature = [] }
            : new GroupMemberBan { GroupId = group.Ref.GroupId, PrevHash = previous, Accounts = [peer.AccountId], DeviceSignature = [] };
        concurrent = concurrent with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(concurrent.GetSigningInput(TestNetwork.Context), Token)] };
        // Reuse the authenticated pool, but exercise a cold key read through the
        // public device signer so the update can pause after reading its basis.
        var protector = new PausedSigningProtector(fixture.Protector);
        await using var devices = new DeviceManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Client.AccountManager, fixture.Account, protector);
        await devices.InitializeAsync(Token);
        await using var manager = new GroupManager(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, devices, fixture.Client.MessageManager, fixture.Protector);
        await manager.InitializeAsync(Token);
        var changed = AsyncTest.Signal();
        manager.GroupChanged += (_, _) => changed.TrySetResult();
        var update = manager.UpdateGroupAsync(group.Ref, new() { Name = "latest" }, Token);
        Task? refresh = null;
        try
        {
            await protector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            using var accepted = host.Respond(new(closed ? "group.close" : "group.member.ban", concurrent.ToJson(), null, closed ? HttpMethod.Delete : HttpMethod.Put));
            refresh = manager.GetGroupAsync(group.Ref, Token);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            protector.Release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (closed) await Assert.ThrowsAsync<InvalidOperationException>(() => update.WaitAsync(TimeSpan.FromSeconds(5), Token));
            else Assert.Equal("latest", (await update.WaitAsync(TimeSpan.FromSeconds(5), Token)).Group.Name);
            await using var database = fixture.Database.Open();
            if (!closed) Assert.Single(await database.GroupBans.ToListAsync(Token));
            var publication = await database.GroupOperations.SingleAsync(value => value.Method == "group.update", Token);
            Assert.Equal(closed, publication.Pending);
            Assert.Null(publication.NextAttemptAt);
            if (closed) Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "group.update");
        }
        finally
        {
            protector.Release.TrySetResult();
            if (refresh is not null) await Record.ExceptionAsync(() => refresh);
            await Record.ExceptionAsync(() => update);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_update_does_not_wait_for_an_uncancelable_sync_from_its_predecessor(bool afterPublication)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new GroupRelay(fixture);
        host.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var reads = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            var response = host.Respond(request);
            if (request.Method == "group.sync" && Interlocked.Increment(ref reads) == (afterPublication ? 2 : 1))
            {
                entered.TrySetResult();
                await release.Task;
            }
            return response;
        };
        var old = manager.UpdateGroupAsync(group.Ref, new() { Name = "old", Description = "retained" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var latest = await manager.UpdateGroupAsync(group.Ref, new() { Name = "latest" }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => old);
            Assert.Equal("latest", latest.Group.Name);
            Assert.Equal("retained", latest.Group.Description);

            var disposal = manager.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using var database = fixture.Database.Open();
            var stored = await database.Groups.SingleAsync(value => value.GroupId == group.Ref.GroupId, Token);
            Assert.Equal("latest", stored.Name);
            Assert.Equal("retained", stored.Description);
            Assert.False((await database.GroupOperations.SingleAsync(value => value.Method == "group.update", Token)).Pending);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task An_update_refetches_when_an_independent_operation_advances_the_timeline_during_its_read()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new GroupRelay(fixture);
        host.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        // Leave an accepted event unsynchronized so the delayed page contains an
        // event that the independent operation will have already applied.
        host.LoseResponseFor = "group.update";
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.UpdateGroupAsync(group.Ref, new() { Description = "retained" }, Token));
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var reads = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            var response = host.Respond(request);
            if (request.Method == "group.sync" && Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return response;
        };
        var update = manager.UpdateGroupAsync(group.Ref, new() { Name = "latest" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await manager.SetNicknameAsync(group.Ref, "member", Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        finally { release.TrySetResult(); }
        var result = await update.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("latest", result.Group.Name);
        Assert.Equal("retained", result.Group.Description);
        await using var database = fixture.Database.Open();
        Assert.Equal(host.Events.Count, await database.GroupEvents.CountAsync(value => value.GroupId == group.Ref.GroupId, Token));
        Assert.False((await database.GroupOperations.SingleAsync(value => value.Method == "group.update", Token)).Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_group_property_updates_merge_and_fence_late_management_requests(bool acceptBeforeDelay)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new GroupRelay(fixture);
        host.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var processed = AsyncTest.Signal();
        var writes = 0;
        fixture.Relay.Handler = async (request, _) =>
        {
            if (request.Method != "group.update" || Interlocked.Increment(ref writes) != 1) return host.Respond(request);
            var response = acceptBeforeDelay ? host.Respond(request) : null;
            entered.TrySetResult();
            await release.Task;
            try { return response ?? host.Respond(request); }
            finally { processed.TrySetResult(); }
        };
        var old = manager.UpdateGroupAsync(group.Ref, new() { Name = "renamed", Description = "old" }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var result = await manager.UpdateGroupAsync(group.Ref, new() { Description = FieldUpdate<string>.Delete, MemberCapacity = 20 }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<StateUpdateSupersededException>(() => old);
            Assert.Equal("renamed", result.Group.Name);
            Assert.Null(result.Group.Description);
            Assert.Equal(20, result.Group.MemberCapacity);
            var count = host.Events.Count;
            release.TrySetResult();
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(count, host.Events.Count);
            await using var database = fixture.Database.Open();
            Assert.False((await database.GroupOperations.SingleAsync(value => value.Method == "group.update", Token)).Pending);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task A_new_group_patch_after_timeout_and_restart_keeps_omitted_fields()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new GroupRelay(fixture);
        host.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        fixture.Relay.Handler = (request, _) => request.Method == "group.update"
            ? throw new TimeoutException("offline") : Task.FromResult(host.Respond(request));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Client.GroupManager.UpdateGroupAsync(group.Ref, new() { Name = "old", Description = "retained" }, Token));
        await fixture.ReopenAsync();
        host.Install();
        var updated = await fixture.Client.GroupManager.UpdateGroupAsync(group.Ref, new() { Name = "latest" }, Token);
        Assert.Equal("latest", updated.Group.Name);
        Assert.Equal("retained", updated.Group.Description);
        Assert.Equal(2, host.Events.Count);
    }

    [Fact]
    public async Task Recovery_of_an_accepted_group_patch_does_not_append_it_twice()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new GroupRelay(fixture);
        host.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        host.LoseResponseFor = "group.update";
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.GroupManager.UpdateGroupAsync(group.Ref, new() { Name = "latest" }, Token));
        await fixture.ReopenAsync();
        var manager = fixture.Client.GroupManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(async () =>
        {
            await using var database = fixture.Database.Open();
            return !(await database.GroupOperations.SingleAsync(value => value.Method == "group.update", Token)).Pending;
        });
        await manager.StopAsync(Token);
        Assert.Equal(2, host.Events.Count);
        Assert.Equal("latest", (await manager.GetGroupAsync(group.Ref, Token)).Group.Name);
    }

    sealed class PausedSigningProtector(ISecretProtector inner) : ISecretProtector
    {
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();
        public Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken = default) =>
            inner.ProtectAsync(plaintext, purpose, cancellationToken);
        public async Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> protectedData, string purpose, CancellationToken cancellationToken = default)
        {
            if (purpose.Contains("device-signing", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.UnprotectAsync(protectedData, purpose, cancellationToken);
        }
    }
}
