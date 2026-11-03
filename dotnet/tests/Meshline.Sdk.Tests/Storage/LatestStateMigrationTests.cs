using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Meshline.Tests.Storage;

public sealed class LatestStateMigrationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Profile_diagnostics_upgrade_preserves_pending_intent_request_generation_and_schedule()
    {
        await using var storage = new TestDatabase();
        var deadline = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        var intent = "{\"nickname\":\"pending\",\"bio\":null}";
        Directory.CreateDirectory(Path.GetDirectoryName(storage.Options.Path)!);
        await using (var previous = new MeshlineDbContext(storage.Options, SqliteOpenMode.ReadWriteCreate))
        {
            await previous.GetService<IMigrator>().MigrateAsync("20261030132411_LatestStatePublications", Token);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO SignedRequests (Method, RelayId, DocumentJson, Revision, Pending, IntentJson, Generation, RequestState, NextAttemptAt)
                VALUES ('profile.publish', 'relay', 'exact-signed-request', 42, 1, {intent}, 7, 1, {deadline.UtcTicks})
                """, Token);
        }
        await storage.MigrateAsync();
        await using var upgraded = storage.Open();
        var row = await upgraded.SignedRequests.SingleAsync(Token);
        Assert.Equal("exact-signed-request", row.DocumentJson);
        Assert.Equal("{\"nickname\":\"pending\",\"bio\":null}", row.IntentJson);
        Assert.Equal(7, row.Generation);
        Assert.Equal(42, row.Revision);
        Assert.True(row.Pending);
        Assert.Equal(PublicationRequestState.Prepared, row.RequestState);
        Assert.Equal(deadline, row.NextAttemptAt);
        Assert.Null(row.LastErrorJson);
        row.LastErrorJson = "persisted diagnostic";
        await upgraded.SaveChangesAsync(Token);
        await using var reopened = storage.Open();
        Assert.Equal("persisted diagnostic", (await reopened.SignedRequests.SingleAsync(Token)).LastErrorJson);
    }

    [Fact]
    public async Task Fresh_schema_uses_existing_publication_tables()
    {
        await using var storage = new TestDatabase();
        await storage.MigrateAsync();
        await using var upgraded = storage.Open();
        var tables = await upgraded.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync(Token);
        Assert.DoesNotContain("StateUpdates", tables);
        foreach (var table in new[] { "SignedRequests", "GroupOperations", "ChannelOperations" })
        {
            var columns = await upgraded.Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table})").ToListAsync(Token);
            Assert.DoesNotContain("Failures", columns);
            Assert.DoesNotContain("Paused", columns);
            Assert.DoesNotContain("RequestGeneration", columns);
            Assert.DoesNotContain("RequestInvalidated", columns);
            Assert.Contains("RequestState", columns);
            Assert.Contains("NextAttemptAt", columns);
        }
        Assert.Equal(upgraded.Database.GetMigrations(), await upgraded.Database.GetAppliedMigrationsAsync(Token));
        var signedColumns = await upgraded.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('SignedRequests')").ToListAsync(Token);
        Assert.Contains("LastErrorJson", signedColumns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Publication_state_and_pending_flag_survive_reopening_without_database_generated_values(bool prepared, bool pending)
    {
        await using var storage = new TestDatabase();
        await storage.MigrateAsync();
        var state = prepared ? PublicationRequestState.Prepared : PublicationRequestState.None;
        var request = prepared ? "signed-request" : null;
        await using (var write = storage.Open())
        {
            write.SignedRequests.Add(new()
            {
                Method = "profile.publish",
                RelayId = "relay",
                DocumentJson = request,
                RequestState = state,
                Pending = pending
            });
            write.GroupOperations.Add(new()
            {
                GroupId = "group",
                Method = "group.update",
                RequestJson = request,
                RequestState = state,
                Pending = pending
            });
            write.ChannelOperations.Add(new()
            {
                ResourceId = "channel",
                RelayId = "relay",
                Method = "channel.update",
                DocumentJson = request,
                RequestState = state,
                Pending = pending
            });
            await write.SaveChangesAsync(Token);
        }
        await using var read = storage.Open();
        IStatePublication[] rows = [await read.SignedRequests.SingleAsync(Token), await read.GroupOperations.SingleAsync(Token), await read.ChannelOperations.SingleAsync(Token)];
        Assert.All(rows, row =>
        {
            Assert.Equal(state, row.RequestState);
            Assert.Equal(pending, row.Pending);
            Assert.Equal(request, row.RequestJson);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_group_and_channel_publications_resume_without_replaying_accepted_changes(bool group)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var groups = new GroupRelay(fixture);
        var channels = new ChannelRelay(fixture);
        string id;
        string method;
        if (group)
        {
            groups.Install();
            var created = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId,
                new() { Name = "initial", MemberCapacity = 10, InvitePolicy = GroupInvitePolicy.Administrators }, Token);
            id = created.Ref.GroupId;
            method = "group.update";
            groups.LoseResponseFor = method;
            await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.GroupManager.UpdateGroupAsync(created.Ref, new() { Name = "retained" }, Token));
        }
        else
        {
            channels.Install();
            var created = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "initial", cancellationToken: Token);
            id = created.Ref.ChannelId;
            method = "channel.update";
            fixture.Relay.Handler = (request, _) =>
            {
                var response = channels.Respond(request);
                if (request.Method == method) { response.Dispose(); throw new HttpRequestException("lost response"); }
                return Task.FromResult(response);
            };
            await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ChannelManager.UpdateChannelAsync(created.Ref, new() { Name = "retained" }, Token));
            channels.Install();
        }
        var json = fixture.Relay.Requests.Last(value => value.Method == method).Body!;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        var manager = group ? (Meshline.Components.ClientComponent)fixture.Client.GroupManager : fixture.Client.ChannelManager;
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(1)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
        await AsyncTest.UntilAsync(async () =>
        {
            await using var database = fixture.Database.Open();
            return group
                ? !(await database.GroupOperations.SingleAsync(value => value.GroupId == id && value.Method == method, Token)).Pending
                : !(await database.ChannelOperations.SingleAsync(value => value.ResourceId == id && value.Method == method, Token)).Pending;
        });
        await manager.StopAsync(Token);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == method);
        await using var read = fixture.Database.Open();
        IStatePublication state = group
            ? await read.GroupOperations.SingleAsync(value => value.GroupId == id && value.Method == method, Token)
            : await read.ChannelOperations.SingleAsync(value => value.ResourceId == id && value.Method == method, Token);
        Assert.Null(state.IntentJson);
        Assert.False(state.Pending);
        Assert.Equal(json, state.RequestJson);
        Assert.Equal(1, state.Generation);
    }
}
