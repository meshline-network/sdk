using Meshline.Backups;
using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupBackupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Fact]
    public async Task Group_import_without_a_device_retains_records_before_secrets_and_resumes_after_creation()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync(establish: false);
        var relay = new GroupRelay(source);
        relay.Install();
        var group = await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId, new()
        {
            Name = "restore before device",
            MemberCapacity = 10
        }, Token);
        var records = new List<JsonElement>();
        await foreach (var record in source.Client.GroupManager.ExportBackupAsync(cancellationToken: Token))
            records.Add(record);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => target.Client.GroupManager.ImportBackupAsync(Replay(records), Token));
        Assert.Equal("No local device has been created.", error.Message);
        await using (var db = target.Database.Open())
        {
            Assert.Equal(group.Ref.GroupId, (await db.Groups.SingleAsync(Token)).GroupId);
            Assert.Single(await db.GroupMembers.ToListAsync(Token));
            Assert.Single(await db.GroupEvents.ToListAsync(Token));
            Assert.Empty(await db.GroupMemberKeys.ToListAsync(Token));
            var epoch = await db.GroupEpochs.SingleAsync(Token);
            Assert.Null(epoch.ProtectedClientSecret);
            Assert.Null(epoch.ProtectedApplicationSecret);
        }
        var prefix = new List<JsonElement>();
        await foreach (var record in target.Client.GroupManager.ExportBackupAsync(cancellationToken: Token))
            prefix.Add(record);
        Assert.Equal(records.TakeWhile(record => record.GetProperty("type").GetString() != "group_epoch").Select(record => record.GetRawText()),
            prefix.Where(record => record.GetProperty("type").GetString() != "group_epoch").Select(record => record.GetRawText()));
        var retainedEpoch = BackupData.Read<BackupData.EpochData>(Assert.Single(prefix, record => record.GetProperty("type").GetString() == "group_epoch").GetProperty("data"));
        Assert.Null(retainedEpoch.ClientSecret);
        Assert.Null(retainedEpoch.ApplicationSecret);

        await target.Client.EstablishAccountAsync(new()
        {
            RelayId = target.Relay.RelayId
        }, Token);
        Assert.True((await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied > 0);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied);
        var restored = new List<JsonElement>();
        await foreach (var record in target.Client.GroupManager.ExportBackupAsync(cancellationToken: Token))
            restored.Add(record);
        Assert.Equal(records.Select(record => record.GetRawText()), restored.Select(record => record.GetRawText()));
    }

    [Fact]
    public async Task Group_export_with_missing_local_device_fails_only_when_unprotecting_a_secret()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var relay = new GroupRelay(fixture);
        relay.Install();
        await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new()
        {
            Name = "retained history",
            MemberCapacity = 10
        }, Token);
        await fixture.Client.DisposeAsync();
        await using (var db = fixture.Database.Open())
            await db.LocalDevices.ExecuteDeleteAsync(Token);
        await fixture.ReopenAsync();

        var records = new List<JsonElement>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var record in fixture.Client.GroupManager.ExportBackupAsync(cancellationToken: Token))
                records.Add(record);
        });
        Assert.Equal("No local device has been created.", error.Message);
        Assert.Contains(records, record => record.GetProperty("type").GetString() == "group_event");
        Assert.DoesNotContain(records, record => record.GetProperty("type").GetString() == "group_epoch");
        Assert.DoesNotContain(records, record => record.GetProperty("type").GetString() == "group_secret");
    }

    [Fact]
    public async Task Group_restore_notifies_visible_changes_before_failure_and_keeps_internal_material_silent_on_retry()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        var relay = new GroupRelay(source);
        relay.Install();
        var group = await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId, new()
        {
            Name = "restored",
            MemberCapacity = 10
        }, Token);
        var sent = await source.Client.GroupManager.SendMessageAsync(group.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "history"
            }
        }, Token);
        using var banned = new AccountSigner();
        await source.Client.GroupManager.BanAsync(group.Ref, [banned.AccountId], Token);
        var records = new List<JsonElement>();
        await foreach (var record in source.Client.GroupManager.ExportBackupAsync(BackupSelection.All, Token))
            records.Add(record);
        var changes = new List<(object? Sender, GroupChangedEventArgs Change)>();
        var messages = new List<(object? Sender, GroupMessageInfo Message)>();
        var committedMessages = new List<bool>();
        target.Client.GroupManager.GroupChanged += (sender, change) => changes.Add((sender, change));
        target.Client.GroupManager.TimelineChanged += (sender, change) =>
        {
            foreach (var message in change.Messages)
            {
                messages.Add((sender, message));
                using var db = target.Database.Open();
                committedMessages.Add(db.GroupEvents.Any(row => row.GroupId == message.Group.GroupId && row.Sequence == message.Sequence));
            }
        };
        var requests = target.Relay.Requests.Count;
        await Assert.ThrowsAsync<IOException>(() => target.Client.GroupManager.ImportBackupAsync(Interrupted(), Token));
        Assert.Equal(new[] { GroupChangeKind.Properties | GroupChangeKind.Members | GroupChangeKind.Roles | GroupChangeKind.Status, GroupChangeKind.Bans | GroupChangeKind.Members },
            changes.Select(value => value.Change.ChangeKind));
        Assert.All(changes, value => { Assert.Same(target.Client.GroupManager, value.Sender); Assert.Equal(group.Ref.GroupId, value.Change.Group.Ref.GroupId); });
        var notification = Assert.Single(messages);
        Assert.Same(target.Client.GroupManager, notification.Sender);
        Assert.Equal(sent.Sequence, notification.Message.Sequence);
        Assert.Equal("history", notification.Message.Body!.Text);
        Assert.True(Assert.Single(committedMessages));
        Assert.True((await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied > 0);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied);
        Assert.Equal(2, changes.Count);
        Assert.Single(messages);
        Assert.Equal(requests, target.Relay.Requests.Count);

        async IAsyncEnumerable<JsonElement> Interrupted()
        {
            foreach (var record in records)
            {
                if (record.GetProperty("type").GetString() == "group_secret")
                    throw new IOException("interrupted before secrets");
                yield return record;
            }
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Group_component_snapshot_keeps_events_nicknames_and_keys_together()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        var relay = new GroupRelay(source);
        relay.Install();
        await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId, new()
        {
            Name = "original",
            MemberCapacity = 10
        }, Token);
        var records = new List<JsonElement>();
        await using (var reader = source.Client.GroupManager.ExportBackupAsync(cancellationToken: Token).GetAsyncEnumerator(Token))
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal("group", reader.Current.GetProperty("type").GetString());
            records.Add(reader.Current);
            await using (var db = source.Database.Open())
            {
                (await db.Groups.SingleAsync(Token)).Name = "later";
                (await db.GroupMembers.SingleAsync(Token)).Nickname = "later nickname";
                await db.SaveChangesAsync(Token);
            }
            while (await reader.MoveNextAsync())
                records.Add(reader.Current);
        }
        var notifications = new List<(object? Sender, string Id)>();
        target.Client.GroupManager.GroupChanged += (sender, change) => notifications.Add((sender, change.Group.Ref.GroupId));
        await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        await using var check = target.Database.Open();
        var group = await check.Groups.SingleAsync(Token);
        Assert.Equal("original", group.Name);
        Assert.Null((await check.GroupMembers.SingleAsync(Token)).Nickname);
        Assert.NotNull((await check.GroupEpochs.SingleAsync(Token)).ProtectedApplicationSecret);
        Assert.NotEmpty(notifications);
        Assert.All(notifications, item => { Assert.Same(target.Client.GroupManager, item.Sender); Assert.Equal(group.GroupId, item.Id); });
    }

    [Theory]
    [InlineData("group_event")]
    [InlineData("group_nickname")]
    [InlineData("group_epoch")]
    [InlineData("group_secret")]
    public async Task Group_records_and_secrets_resume_after_restart(string failBefore)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var banned = new AccountSigner();
        var relay = new GroupRelay(source);
        relay.Install();
        var group = await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId, new()
        {
            Name = "restorable",
            MemberCapacity = 10
        }, Token);
        await source.Client.GroupManager.SetNicknameAsync(group.Ref, "restored nickname", Token);
        await source.Client.GroupManager.BanAsync(group.Ref, [banned.AccountId], Token);
        var records = new List<JsonElement>();
        await foreach (var record in source.Client.GroupManager.ExportBackupAsync(cancellationToken: Token))
            records.Add(record);
        await Assert.ThrowsAsync<IOException>(() => target.Client.GroupManager.ImportBackupAsync(Broken(), Token));
        await using (var db = target.Database.Open())
        {
            var pending = await db.Groups.SingleAsync(Token);
            Assert.Equal(failBefore == "group_event", pending.Sequence == -1);
        }
        await target.ReopenAsync();
        // An interrupted prefix remains a valid local state and can be exported or resumed.
        await foreach (var _ in target.Client.GroupManager.ExportBackupAsync(cancellationToken: Token))
        {
        }
        await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied);
        await using var check = target.Database.Open();
        Assert.Equal("restored nickname", (await check.GroupMembers.SingleAsync(Token)).Nickname);
        Assert.Single(await check.GroupMembers.ToListAsync(Token));
        Assert.Single(await check.GroupBans.ToListAsync(Token));
        Assert.Single(await check.GroupMemberKeys.ToListAsync(Token));
        var epoch = await check.GroupEpochs.SingleAsync(Token);
        Assert.NotNull(epoch.ProtectedClientSecret);
        Assert.NotNull(epoch.ProtectedApplicationSecret);

        async IAsyncEnumerable<JsonElement> Broken()
        {
            foreach (var record in records)
            {
                if (record.GetProperty("type").GetString() == failBefore)
                    throw new IOException("interrupted");
                yield return record;
            }
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Missing_management_event_retains_verified_prefix_and_retry_completes_it()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        var relay = new GroupRelay(source);
        relay.Install();
        var group = await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId, new()
        {
            Name = "chain",
            MemberCapacity = 10
        }, Token);
        using var banned = new AccountSigner();
        await source.Client.GroupManager.BanAsync(group.Ref, [banned.AccountId], Token);
        await source.Client.GroupManager.UnbanAsync(group.Ref, [banned.AccountId], Token);
        var records = new List<JsonElement>();
        await foreach (var record in source.Client.GroupManager.ExportBackupAsync(cancellationToken: Token))
            records.Add(record);
        await Assert.ThrowsAsync<InvalidDataException>(() => target.Client.GroupManager.ImportBackupAsync(Replay(records.Where(row =>
            row.GetProperty("type").GetString() != "group_event" || row.GetProperty("data").GetProperty("sequence").GetInt64() != 1)), Token));
        await using (var db = target.Database.Open())
        {
            Assert.Equal(0, (await db.Groups.SingleAsync(Token)).Sequence);
            Assert.Single(await db.GroupEvents.ToListAsync(Token));
            Assert.Empty(await db.GroupBans.ToListAsync(Token));
        }
        await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        await using var check = target.Database.Open();
        Assert.Equal(2, (await check.Groups.SingleAsync(Token)).Sequence);
        Assert.Equal(3, await check.GroupEvents.CountAsync(Token));
        Assert.Empty(await check.GroupBans.ToListAsync(Token));
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
            yield return record;
        await Task.CompletedTask;
    }
}
