using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Backups;

public sealed class BackupSelectionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "backup test password 🔐";
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData("none", "none")]
    [InlineData("none", "all")]
    [InlineData("none", "only")]
    [InlineData("all", "none")]
    [InlineData("all", "all")]
    [InlineData("all", "only")]
    [InlineData("only", "none")]
    [InlineData("only", "all")]
    [InlineData("only", "only")]
    public async Task All_history_selections_preserve_base_data_and_merge_only_selected_history(string directMode, string groupMode)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        var (peers, groups) = await SeedAsync(source);
        StoredMessageRecord existing;
        await using (var db = source.Database.Open())
            existing = await db.Messages.AsNoTracking().SingleAsync(value => value.Sender == peers[1], Token);
        existing.LocalSequence = 0;
        await using (var db = target.Database.Open())
        {
            db.Messages.Add(existing);
            await db.SaveChangesAsync(Token);
        }
        var direct = Select(directMode, peers);
        var group = Select(groupMode, groups);
        // The default overload must produce a base-data-only backup, with no implicit history.
        var options = directMode == "none" && groupMode == "none" ? null : new BackupOptions { DirectHistory = direct, GroupHistory = group };
        using var output = new MemoryStream();
        await source.Client.ExportBackupAsync(output, Password, options, Token);
        var records = await ReadAsync(output.ToArray());
        Assert.Equal(directMode, records[0].GetProperty("data").GetProperty("direct_history").GetProperty("mode").GetString());
        Assert.Equal(groupMode, records[0].GetProperty("data").GetProperty("group_history").GetProperty("mode").GetString());
        var expectedPeers = directMode == "all" ? peers : directMode == "only" ? [peers[0], peers[2]] : Array.Empty<string>();
        var expectedGroups = groupMode == "all" ? groups : groupMode == "only" ? [groups[0], groups[2]] : Array.Empty<string>();
        var messages = records.Where(value => value.GetProperty("type").GetString() == "message").ToArray();
        Assert.Equal(expectedPeers.Length * 2, messages.Length);
        foreach (var peer in expectedPeers)
        {
            Assert.Single(messages, value => value.GetProperty("data").GetProperty("sender").GetString() == peer);
            Assert.Single(messages, value => value.GetProperty("data").GetProperty("recipient").GetString() == peer);
        }
        var chatEvents = records.Where(IsGroupChat).ToArray();
        Assert.Equal(expectedGroups.Length, chatEvents.Length);
        Assert.All(chatEvents, value => Assert.Contains(value.GetProperty("data").GetProperty("group_id").GetString(), expectedGroups));
        Assert.Equal(6, records.Count(value => value.GetProperty("type").GetString() == "group_event" && !IsGroupChat(value)));
        Assert.Equal(2, records.Count(value => value.GetProperty("type").GetString() == "contact"));
        Assert.Equal(3, records.Count(value => value.GetProperty("type").GetString() == "group"));
        Assert.DoesNotContain(records, value => value.GetProperty("type").GetString() is "group_member" or "group_ban" or "group_checkpoint");
        Assert.Equal(3, records.Count(value => value.GetProperty("type").GetString() == "group_epoch"));
        Assert.Equal(3, records.Count(value => value.GetProperty("type").GetString() == "group_secret"));
        Assert.Equal(2, records.Count(value => value.GetProperty("type").GetString() == "channel"));
        output.Position = 0;
        var imported = await target.Client.ImportBackupAsync(output, Password, Token);
        Assert.Equal(0, imported.Conflicts);
        output.Position = 0;
        Assert.Equal(0, (await target.Client.ImportBackupAsync(output, Password, Token)).Applied);
        await using var check = target.Database.Open();
        Assert.Equal(directMode == "all" ? 6 : directMode == "only" ? 5 : 1, await check.Messages.CountAsync(Token));
        Assert.Equal(6 + expectedGroups.Length, await check.GroupEvents.CountAsync(Token));
        Assert.Equal(3, await check.GroupBans.CountAsync(Token));
        Assert.Equal(3, await check.GroupMembers.CountAsync(Token));
        Assert.Equal(2, await check.Contacts.CountAsync(Token));
        Assert.Equal(ContactRelationshipState.Deleted, (await check.Contacts.FindAsync([peers[1]], Token))!.State);
        Assert.False(await check.Contacts.AnyAsync(value => value.AccountId == peers[2], Token));
        Assert.Equal(3, await check.Groups.CountAsync(Token));
        Assert.Equal(3, await check.GroupMemberKeys.CountAsync(Token));
        Assert.Equal(3, await check.GroupEpochs.CountAsync(value => value.ProtectedClientSecret != null && value.ProtectedApplicationSecret != null, Token));
        Assert.Equal(2, await check.Channels.CountAsync(Token));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("empty")]
    [InlineData("absent")]
    public async Task Empty_or_unavailable_history_selections_export_only_base_data(string kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await source.InitializeAsync();
        await SeedAsync(source);
        using var absent = new AccountSigner();
        var options = kind switch
        {
            "empty" => new BackupOptions { DirectHistory = BackupSelection.Only(), GroupHistory = BackupSelection.Only() },
            "absent" => new BackupOptions { DirectHistory = BackupSelection.Only(absent.AccountId), GroupHistory = BackupSelection.Only("grp_AAAAAAAAAAAAAAAAAAAAAA") },
            _ => new BackupOptions()
        };
        using var output = new MemoryStream();
        await source.Client.ExportBackupAsync(output, Password, options, Token);
        var records = await ReadAsync(output.ToArray());
        Assert.DoesNotContain(records, value => value.GetProperty("type").GetString() == "message" || IsGroupChat(value));
        Assert.Equal(3, records.Count(value => value.GetProperty("type").GetString() == "group"));
        Assert.Equal(3, records.Count(value => value.GetProperty("type").GetString() == "group_secret"));
        Assert.Equal(2, records.Count(value => value.GetProperty("type").GetString() == "contact"));
        Assert.Equal(2, records.Count(value => value.GetProperty("type").GetString() == "channel"));
    }

    [Theory]
    [InlineData("direct_kind")]
    [InlineData("group_kind")]
    [InlineData("null_direct")]
    [InlineData("null_group")]
    [InlineData("null_id")]
    [InlineData("empty_id")]
    [InlineData("oversize")]
    public async Task Invalid_selections_fail_before_changing_the_destination_and_allow_retry(string kind)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await source.InitializeAsync();
        var options = kind switch
        {
            "direct_kind" => new BackupOptions { DirectHistory = BackupSelection.Only("grp_AAAAAAAAAAAAAAAAAAAAAA") },
            "group_kind" => new BackupOptions { GroupHistory = BackupSelection.Only(source.Account.AccountId) },
            "null_direct" => new BackupOptions { DirectHistory = null! },
            "null_group" => new BackupOptions { GroupHistory = null! },
            "null_id" => new BackupOptions { DirectHistory = BackupSelection.Only([null!]) },
            "empty_id" => new BackupOptions { GroupHistory = BackupSelection.Only("") },
            _ => new BackupOptions
            {
                GroupHistory = BackupSelection.Only(Enumerable.Range(0, 160_000).Select(index =>
            {
                var bytes = new byte[16];
                BinaryPrimitives.WriteInt32BigEndian(bytes, index);
                return "grp_" + Base64Url.EncodeToString(bytes);
            }).ToArray())
            }
        };
        using var output = new MemoryStream();
        await output.WriteAsync(new byte[] { 1, 2, 3 }, Token);
        if (kind == "oversize")
            await Assert.ThrowsAsync<InvalidDataException>(() => source.Client.ExportBackupAsync(output, Password, options, Token));
        else
            await Assert.ThrowsAsync<ArgumentException>(() => source.Client.ExportBackupAsync(output, Password, options, Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
        Assert.Equal(3, output.Position);
        await source.Client.ExportBackupAsync(output, Password, cancellationToken: Token);
        Assert.True(output.Length > 3);
    }

    [Theory]
    [InlineData("direct_history", "none", false)]
    [InlineData("direct_history", "only", false)]
    [InlineData("group_history", "none", false)]
    [InlineData("group_history", "only", false)]
    [InlineData("group_history", "none", true)]
    public async Task Out_of_scope_history_retains_preceding_base_records_even_when_the_group_would_be_skipped(string field, string mode, bool conflictingRelay)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        var (peers, _) = await SeedAsync(source);
        using var original = new MemoryStream();
        await source.Client.ExportBackupAsync(original, Password, new()
        {
            DirectHistory = BackupSelection.All,
            GroupHistory = BackupSelection.All
        }, Token);
        if (conflictingRelay)
        {
            await using var db = source.Database.Open();
            var group = await db.Groups.AsNoTracking().OrderBy(value => value.GroupId).FirstAsync(Token);
            group.RelayId = "0x" + new string('1', 40);
            await using var targetDb = target.Database.Open();
            targetDb.Groups.Add(group);
            await targetDb.SaveChangesAsync(Token);
        }
        var bytes = await BackupFormatSetup.RewriteRecordsAsync(original.ToArray(), Password, Token, (type, data) =>
        {
            if (type != "manifest")
                return data;
            var manifest = JsonNode.Parse(data.GetRawText())!;
            manifest[field] = JsonSerializer.SerializeToNode(new
            {
                mode,
                ids = mode == "only" ? [field == "direct_history" ? peers[0] : "grp_AAAAAAAAAAAAAAAAAAAAAA"] : Array.Empty<string>()
            });
            return JsonSerializer.SerializeToElement(manifest);
        });
        var inserted = 0;
        using var observer = new DatabaseObserver((name, value) =>
        {
            if (name.EndsWith("CommandExecuted", StringComparison.Ordinal) && value is CommandExecutedEventData command
                && command.Command.Connection?.DataSource == target.Database.Options.Path
                && command.Command.CommandText.StartsWith("INSERT INTO \"Contacts\"", StringComparison.Ordinal))
                inserted++;
        });
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => target.Client.ImportBackupAsync(input, Password, Token));
        Assert.Equal(2, inserted);
        await using var check = target.Database.Open();
        Assert.Equal(2, await check.Contacts.CountAsync(Token));
        Assert.Equal(field == "direct_history" ? (mode == "only" ? 2 : 0) : 6, await check.Messages.CountAsync(Token));
        Assert.False(await check.GroupEvents.AnyAsync(value => value.IsMessage, Token));
        Assert.Empty(await check.GroupMemberKeys.ToListAsync(Token));
        Assert.Empty(await check.Channels.ToListAsync(Token));
        Assert.Equal(field == "direct_history" ? (conflictingRelay ? 1 : 0) : 3, await check.Groups.CountAsync(Token));
        original.Position = 0;
        await target.Client.ImportBackupAsync(original, Password, Token);
        Assert.Equal(2, await check.Contacts.CountAsync(Token));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"mode\":\"all\"}")]
    [InlineData("{\"mode\":null,\"ids\":[]}")]
    [InlineData("{\"mode\":\"other\",\"ids\":[]}")]
    [InlineData("{\"mode\":\"all\",\"ids\":[\"grp_AAAAAAAAAAAAAAAAAAAAAA\"]}")]
    [InlineData("{\"mode\":\"none\",\"ids\":[\"grp_AAAAAAAAAAAAAAAAAAAAAA\"]}")]
    [InlineData("{\"mode\":\"only\",\"ids\":null}")]
    [InlineData("{\"mode\":\"only\",\"ids\":[null]}")]
    [InlineData("{\"mode\":\"only\",\"ids\":[\"invalid\"]}")]
    [InlineData("{\"mode\":\"none\",\"ids\":[],\"extra\":true}")]
    public async Task Rejects_malformed_history_declarations(string selection)
    {
        using var time = Clock.Use(new ManualClock());
        await using var target = Create();
        await target.InitializeAsync();
        foreach (var field in new[] { "direct_history", "group_history" })
        {
            using var input = new MemoryStream();
            await using (var writer = await BackupCodec.CreateAsync(input, Password, Token))
            {
                var invalid = JsonSerializer.Deserialize<JsonElement>(selection);
                var none = JsonSerializer.SerializeToElement(new
                {
                    mode = "none",
                    ids = Array.Empty<string>()
                });
                await writer.WriteAsync("manifest", new
                {
                    network = TestNetwork.Context.ToString(),
                    account = target.Account.AccountId,
                    created_at = 0,
                    direct_history = field == "direct_history" ? invalid : none,
                    group_history = field == "group_history" ? invalid : none
                }, Token);
                await writer.WriteAsync("end", new
                {
                    records = 0
                }, Token);
                await writer.CompleteAsync(Token);
            }
            input.Position = 0;
            var error = await Record.ExceptionAsync(() => target.Client.ImportBackupAsync(input, Password, Token));
            Assert.True(error is InvalidDataException or JsonException, error?.ToString());
        }
        await using var check = target.Database.Open();
        Assert.Empty(await check.Groups.ToListAsync(Token));
    }

    [Fact]
    public async Task Imports_the_independent_subset_vector_into_an_established_device()
    {
        using var json = await BackupFormatSetup.ReadVectorAsync(Token);
        var vector = json.RootElement;
        using var manifest = JsonDocument.Parse(vector.GetProperty("records_utf8")[0].GetString()!);
        using var time = Clock.Use(new ManualClock(DateTimeOffset.FromUnixTimeSeconds(manifest.RootElement.GetProperty("data").GetProperty("created_at").GetInt64())));
        // Public test scalar 0x0102...20 owns the fixture's account; it is not contained in the backup.
        await using var target = new TestClient(new AccountSigner(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
        await target.InitializeAsync();
        using var input = new MemoryStream(Convert.FromBase64String(vector.GetProperty("backup_base64").GetString()!));
        var report = await target.Client.ImportBackupAsync(input, vector.GetProperty("password").GetString()!, Token);
        Assert.Equal(2, report.Applied);
        await using var check = target.Database.Open();
        Assert.Single(await check.Contacts.ToListAsync(Token));
        var message = Assert.Single(await check.Messages.ToListAsync(Token));
        Assert.Equal("你好，Meshline", ProtocolModel.FromJson<DirectMessage>(message.PayloadJson!)!.Body!.Text);
    }

    static BackupSelection Select(string mode, string[] ids)
    {
        if (mode == "all")
            return BackupSelection.All;
        if (mode == "none")
            return BackupSelection.None;
        var supplied = new[] { ids[2], ids[0], ids[2] };
        var selection = BackupSelection.Only(supplied);
        supplied[0] = ids[1];
        supplied[1] = "invalid after creation";
        return selection;
    }

    static bool IsGroupChat(JsonElement value) => value.GetProperty("type").GetString() == "group_event"
        && ProtocolModel.FromJson<TypedProtocolModel>(value.GetProperty("data").GetProperty("payload_json").GetString()!) is GroupMessageEnvelope;

    static async Task<List<JsonElement>> ReadAsync(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        var records = new List<JsonElement>();
        while (true)
        {
            var record = await reader.ReadAsync(Token);
            records.Add(record);
            if (record.GetProperty("type").GetString() != "end")
                continue;
            await reader.RequireEndAsync(Token);
            return records;
        }
    }

    static async Task<(string[] Peers, string[] Groups)> SeedAsync(TestClient fixture)
    {
        using var first = new AccountSigner();
        using var second = new AccountSigner();
        using var third = new AccountSigner();
        var peers = new[] { first.AccountId, second.AccountId, third.AccountId };
        await ContactSetup.AcceptPeerAsync(fixture, first, Token);
        var groups = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var relay = new GroupRelay(fixture);
            relay.Install();
            var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new()
            {
                Name = "group " + i,
                MemberCapacity = 10
            }, Token);
            groups.Add(group.Ref.GroupId);
            await fixture.Client.GroupManager.BanAsync(group.Ref, [third.AccountId], Token);
            await fixture.Client.GroupManager.SendMessageAsync(group.Ref, new()
            {
                Body = new()
                {
                    ContentType = "text/plain",
                    Text = "history " + i
                }
            }, Token);
        }
        await fixture.Client.StopAsync(Token);
        await using var db = fixture.Database.Open();
        db.Contacts.Add(new()
        {
            AccountId = second.AccountId,
            State = ContactRelationshipState.Deleted,
            UpdatedAt = Clock.UtcNow
        });
        foreach (var peer in peers)
        {
            db.Messages.Add(Message(peer, fixture.Account.AccountId));
            db.Messages.Add(Message(fixture.Account.AccountId, peer));
        }
        for (var i = 1; i < 3; i++)
        {
            var group = groups[i];
            var row = await db.GroupEvents.SingleAsync(value => value.GroupId == group && value.IsMessage, Token);
            row.IsMessage = false;
            row.DecryptedPayloadJson = null;
            if (i == 2)
                row.Rejection = "unavailable history";
        }
        db.Channels.AddRange(new()
        {
            ChannelId = "chan_AAAAAAAAAAAAAAAAAAAAAA",
            RelayId = fixture.Relay.RelayId,
            IsFollowed = true
        },
            new()
            {
                ChannelId = "chan_AAAAAAAAAAAAAAAAAAAAAg",
                RelayId = fixture.Relay.RelayId,
                IsFollowed = false
            });
        await db.SaveChangesAsync(Token);
        return (peers, groups.ToArray());
    }

    static StoredMessageRecord Message(string sender, string recipient) => new()
    {
        Sender = sender,
        Recipient = recipient,
        SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
        MessageId = Identifiers.CreateMessageId(),
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(Clock.UtcNow.ToUnixTimeSeconds()),
        IsDirect = true,
        PayloadType = "meshline.message.direct",
        PayloadJson = new DirectMessage { Body = new() { ContentType = "text/plain", Text = "history" } }.ToJson()
    };
}
