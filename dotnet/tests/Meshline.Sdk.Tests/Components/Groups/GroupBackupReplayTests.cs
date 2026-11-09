using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Text;
using System.Text.Json;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupBackupReplayTests
{
    const string Password = "backup test password \U0001F510";
    static readonly BackupOptions WithHistory = new() { DirectHistory = BackupSelection.All, GroupHistory = BackupSelection.All };
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Fact]
    public async Task Restored_group_history_remains_consistent_with_live_synchronization()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var relay = new GroupRelay(fixture);
        relay.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new()
        {
            Name = "group",
            MemberCapacity = 10
        }, Token);
        GroupRecord initialState;
        await using (var db = fixture.Database.Open())
            initialState = await db.Groups.AsNoTracking().SingleAsync(Token);
        await fixture.Client.GroupManager.SendMessageAsync(group.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "retained"
            }
        }, Token);
        await fixture.Client.StopAsync(Token);
        using var backup = new MemoryStream();
        await fixture.Client.ExportBackupAsync(backup, Password, WithHistory, cancellationToken: Token);
        await using (var db = fixture.Database.Open())
        {
            db.Entry(await db.Groups.SingleAsync(Token)).CurrentValues.SetValues(initialState);
            await db.GroupEvents.Where(row => row.Sequence > initialState.Sequence).ExecuteDeleteAsync(Token);
            await db.SaveChangesAsync(Token);
        }
        backup.Position = 0;
        await fixture.Client.ImportBackupAsync(backup, Password, Token);
        await fixture.ReopenAsync();
        await fixture.Client.GroupManager.SynchronizeAsync(group.Ref, Token);
        await using var check = fixture.Database.Open();
        Assert.Equal(1, await check.GroupEvents.CountAsync(row => row.IsMessage, Token));
        Assert.Equal(relay.Events[^1].Sequence, (await check.Groups.SingleAsync(Token)).Sequence);
    }

    [Fact]
    public async Task Fixed_backup_records_rebuild_every_management_projection_without_chat_or_network()
    {
        using var time = Clock.Use(new ManualClock());
        using var vector = await BackupFormatSetup.ReadVectorAsync(Token);
        var data = vector.RootElement.GetProperty("group_restore");
        await using var target = new TestClient(new AccountSigner(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
        await target.InitializeAsync();
        Assert.Equal(target.Account.AccountId, data.GetProperty("account").GetString());
        var records = data.GetProperty("records").EnumerateArray().ToArray();
        var expected = data.GetProperty("expected_steps").EnumerateArray().ToArray();
        var requests = target.Relay.Requests.Count;
        await target.Client.GroupManager.ImportBackupAsync(Replay([records[0]]), Token);
        for (var i = 1; i < records.Length; i++)
        {
            Assert.Equal(1, (await target.Client.GroupManager.ImportBackupAsync(Replay([records[i]]), Token)).Applied);
            await using var db = target.Database.Open();
            var group = await db.Groups.SingleAsync(Token);
            var step = expected[i - 1];
            var projection = step.GetProperty("projection");
            Assert.Equal(step.GetProperty("sequence").GetInt64(), group.Sequence);
            Assert.Equal(step.GetProperty("epoch").GetInt64(), group.Epoch);
            Assert.Equal(projection.GetProperty("head").GetString(), group.ManagementHash);
            Assert.Equal(projection.GetProperty("name").GetString(), group.Name);
            Assert.Equal(projection.GetProperty("description").GetString(), group.Description);
            Assert.Equal(projection.GetProperty("member_capacity").GetInt64(), group.MemberCapacity);
            Assert.Equal(projection.GetProperty("client_secret_commitment").GetString(), group.Commitment);
            Assert.Equal(projection.GetProperty("status").GetString(), group.Status.ToString().ToLowerInvariant());
            var members = await db.GroupMembers.ToListAsync(Token);
            Assert.Equal(projection.GetProperty("members").EnumerateObject().Count(), members.Count);
            Assert.Equal(members.Count, group.MemberCount);
            foreach (var member in members)
            {
                var wanted = projection.GetProperty("members").GetProperty(member.AccountId);
                Assert.Equal(wanted.GetProperty("role").GetString(), member.Role.ToString().ToLowerInvariant());
                Assert.Equal(Base64Url.DecodeFromChars(wanted.GetProperty("member_encryption_public_key").GetString()!), member.PublicKey);
            }
            Assert.Equal(projection.GetProperty("bans").EnumerateArray().Select(value => value.GetString()).Order(),
                await db.GroupBans.OrderBy(value => value.AccountId).Select(value => value.AccountId).ToListAsync(Token));
        }
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied);
        Assert.Equal(requests, target.Relay.Requests.Count);
        await using var check = target.Database.Open();
        Assert.Empty(await check.GroupOperations.ToListAsync(Token));
        Assert.Empty(await check.MessageOutbox.ToListAsync(Token));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("chain")]
    [InlineData("actor")]
    [InlineData("epoch")]
    [InlineData("rejected")]
    public async Task Invalid_management_never_advances_the_committed_projection(string damage)
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
            Name = "original",
            MemberCapacity = 10
        }, Token);
        var records = await ExportAsync(source);
        await using var db = source.Database.Open();
        var state = await db.Groups.SingleAsync(Token);
        var update = new Meshline.Models.Protocol.GroupUpdate
        {
            GroupId = group.Ref.GroupId,
            PrevHash = damage == "chain" ? "sha256:" + Base64Url.EncodeToString(new byte[32]) : state.ManagementHash!,
            Name = "invalid",
            DeviceSignature = []
        };
        using var outsider = new AccountSigner();
        var signer = new DeviceSigner(outsider, source.Relay.Clock);
        var signature = damage == "signature" ? new byte[64] : damage == "actor"
            ? await signer.SignAsync(update.GetSigningInput(TestNetwork.Context, "device_signature"), Token)
            : await source.Client.DeviceManager.SignAsync(update.GetSigningInput(TestNetwork.Context, "device_signature"), Token);
        update = update with
        {
            DeviceSignature = [.. signature]
        };
        var invalid = new GroupEventRecord
        {
            GroupId = group.Ref.GroupId,
            Sequence = 1,
            Epoch = damage == "epoch" ? 1 : 0,
            PayloadJson = update.ToJson(),
            CertificateJson = (damage == "actor" ? signer.Certificate : source.Client.Device!).ToJson(),
            Rejection = damage == "rejected" ? "bypass" : null
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => target.Client.GroupManager.ImportBackupAsync(
            Replay([.. records.Take(2), BackupData.Record("group_event", BackupData.Project(invalid, BackupData.EventFields))]), Token));
        await using var check = target.Database.Open();
        Assert.Equal("original", (await check.Groups.SingleAsync(Token)).Name);
        Assert.Equal(0, (await check.Groups.SingleAsync(Token)).Sequence);
        Assert.Single(await check.GroupMembers.ToListAsync(Token));
        Assert.Single(await check.GroupEvents.ToListAsync(Token));
    }

    [Theory]
    [InlineData("projection")]
    [InlineData("confirmation")]
    [InlineData("cancellation")]
    public async Task Event_projection_and_operation_confirmation_commit_together_and_resume(string failure)
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
            Name = "atomic",
            MemberCapacity = 10
        }, Token);
        using var peer = new AccountSigner();
        await source.Client.GroupManager.BanAsync(group.Ref, [peer.AccountId], Token);
        var records = await ExportAsync(source);
        var ban = records.Single(record => record.GetProperty("type").GetString() == "group_event" && record.GetProperty("data").GetProperty("sequence").GetInt64() == 1);
        await using (var db = target.Database.Open())
        {
            db.GroupOperations.Add(new()
            {
                GroupId = group.Ref.GroupId,
                Method = "group.member.ban",
                RequestJson = ban.GetProperty("data").GetProperty("payload_json").GetString()
            });
            await db.SaveChangesAsync(Token);
        }
        var changes = 0;
        target.Client.GroupManager.GroupChanged += (_, _) => changes++;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using (var trigger = failure == "cancellation" ? null : await target.Database.CreateTriggerAsync("fail_group_import",
            failure == "projection" ? "BEFORE INSERT ON GroupBans BEGIN SELECT RAISE(ABORT, 'injected failure'); END"
                : "BEFORE DELETE ON GroupOperations BEGIN SELECT RAISE(ABORT, 'injected failure'); END"))
        {
            if (failure == "cancellation")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Client.GroupManager.ImportBackupAsync(Interrupted(), cancellation.Token));
            else
                await Assert.ThrowsAsync<DbUpdateException>(() => target.Client.GroupManager.ImportBackupAsync(Replay(records), Token));
        }
        Assert.Equal(1, changes);
        await using (var db = target.Database.Open())
        {
            Assert.Equal(0, (await db.Groups.SingleAsync(Token)).Sequence);
            Assert.Single(await db.GroupEvents.ToListAsync(Token));
            Assert.Empty(await db.GroupBans.ToListAsync(Token));
            Assert.Single(await db.GroupOperations.ToListAsync(Token));
        }
        await target.ReopenAsync();
        await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied);
        await using var check = target.Database.Open();
        Assert.Equal(1, (await check.Groups.SingleAsync(Token)).Sequence);
        Assert.Equal(2, await check.GroupEvents.CountAsync(Token));
        Assert.Single(await check.GroupBans.ToListAsync(Token));
        Assert.Empty(await check.GroupOperations.ToListAsync(Token));

        async IAsyncEnumerable<JsonElement> Interrupted()
        {
            for (var i = 0; i < records.Count; i++)
            {
                if (i == 2)
                    cancellation.Cancel();
                yield return records[i];
            }
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Older_and_newer_backups_merge_without_replaying_or_reverting_existing_state()
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
            Name = "original",
            MemberCapacity = 10
        }, Token);
        var older = await ExportAsync(source);
        await target.Client.GroupManager.ImportBackupAsync(Replay(older), Token);
        using var peer = new AccountSigner();
        await source.Client.GroupManager.BanAsync(group.Ref, [peer.AccountId], Token);
        await source.Client.GroupManager.UpdateGroupAsync(group.Ref, new()
        {
            Name = "newer"
        }, Token);
        var newer = await ExportAsync(source);
        var changes = 0;
        target.Client.GroupManager.GroupChanged += (_, _) => changes++;
        var requests = target.Relay.Requests.Count;
        await target.Client.GroupManager.ImportBackupAsync(Replay(newer), Token);
        Assert.Equal(2, changes);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(older), Token)).Applied);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(newer), Token)).Applied);
        Assert.Equal(2, changes);
        Assert.Equal(requests, target.Relay.Requests.Count);
        await using var check = target.Database.Open();
        Assert.Equal("newer", (await check.Groups.SingleAsync(Token)).Name);
        Assert.Equal(2, (await check.Groups.SingleAsync(Token)).Sequence);
        Assert.Single(await check.GroupBans.ToListAsync(Token));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("all")]
    [InlineData("only")]
    public async Task Nicknames_restore_independently_of_chat_and_preserve_newer_values_and_join_identity(string selection)
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
            Name = "nickname",
            MemberCapacity = 10
        }, Token);
        await source.Client.GroupManager.SetNicknameAsync(group.Ref, "restored", Token);
        var history = selection == "all" ? BackupSelection.All : selection == "only" ? BackupSelection.Only("grp_AAAAAAAAAAAAAAAAAAAAAA") : BackupSelection.None;
        var records = await ExportAsync(source, history);
        var nickname = Assert.Single(records, value => value.GetProperty("type").GetString() == "group_nickname");
        await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        await using (var db = target.Database.Open())
            Assert.Equal("restored", (await db.GroupMembers.SingleAsync(Token)).Nickname);
        var current = BackupData.Read<BackupData.NicknameData>(nickname.GetProperty("data"));
        var clear = BackupData.Record("group_nickname", current with
        {
            Sequence = current.Sequence + 1,
            Nickname = null
        });
        Assert.Equal(1, (await target.Client.GroupManager.ImportBackupAsync(Replay([clear]), Token)).Applied);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay([nickname]), Token)).Applied);
        var conflict = BackupData.Record("group_nickname", current with
        {
            Sequence = current.Sequence + 1
        });
        Assert.Equal(1, (await target.Client.GroupManager.ImportBackupAsync(Replay([conflict]), Token)).Conflicts);
        var wrongJoin = BackupData.Record("group_nickname", current with
        {
            Sequence = current.Sequence + 2,
            JoinedAtSequence = 1
        });
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay([wrongJoin]), Token)).Applied);
        await using var check = target.Database.Open();
        Assert.Null((await check.GroupMembers.SingleAsync(Token)).Nickname);
        Assert.Equal(selection == "all" ? 2 : 1, await check.GroupEvents.CountAsync(Token));
    }

    [Fact]
    public async Task Conflicting_event_blocks_only_its_group_and_does_not_complete_pending_operations()
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
            Name = "original",
            MemberCapacity = 10
        }, Token);
        await source.Client.GroupManager.UpdateGroupAsync(group.Ref, new()
        {
            Name = "local"
        }, Token);
        await target.Client.GroupManager.ImportBackupAsync(Replay(await ExportAsync(source)), Token);
        using var peer = new AccountSigner();
        await source.Client.GroupManager.BanAsync(group.Ref, [peer.AccountId], Token);
        var records = await ExportAsync(source);
        var index = records.FindIndex(value => value.GetProperty("type").GetString() == "group_event" && value.GetProperty("data").GetProperty("sequence").GetInt64() == 1);
        var record = BackupData.Read<GroupEventRecord>(records[index].GetProperty("data"));
        var changed = ProtocolModel.FromJson<Meshline.Models.Protocol.GroupUpdate>(record.PayloadJson)! with
        {
            Name = "conflicting",
            DeviceSignature = []
        };
        changed = changed with
        {
            DeviceSignature = [.. await source.Client.DeviceManager.SignAsync(changed.GetSigningInput(TestNetwork.Context, "device_signature"), Token)]
        };
        record.PayloadJson = changed.ToJson();
        records[index] = BackupData.Record("group_event", BackupData.Project(record, BackupData.EventFields));
        var otherRelay = new GroupRelay(source);
        otherRelay.Install();
        var other = await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId, new()
        {
            Name = "other",
            MemberCapacity = 10
        }, Token);
        var otherRecords = (await ExportAsync(source)).Where(value => value.GetProperty("data").GetProperty("group_id").GetString() == other.Ref.GroupId);
        await using (var db = target.Database.Open())
        {
            db.GroupOperations.Add(new()
            {
                GroupId = group.Ref.GroupId,
                Method = "group.update",
                RequestJson = changed.ToJson()
            });
            await db.SaveChangesAsync(Token);
        }
        var result = await target.Client.GroupManager.ImportBackupAsync(Replay(records.Concat(otherRecords)), Token);
        Assert.Equal(1, result.Conflicts);
        await using var check = target.Database.Open();
        Assert.Equal("local", (await check.Groups.FindAsync([group.Ref.GroupId], Token))!.Name);
        Assert.Equal(1, (await check.Groups.FindAsync([group.Ref.GroupId], Token))!.Sequence);
        Assert.False(await check.GroupBans.AnyAsync(Token));
        Assert.Equal("other", (await check.Groups.FindAsync([other.Ref.GroupId], Token))!.Name);
        Assert.Single(await check.GroupOperations.ToListAsync(Token));
    }

    [Fact]
    public async Task Adding_older_chat_preserves_newer_projection_read_position_and_event_identity()
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
            Name = "original",
            MemberCapacity = 10
        }, Token);
        await source.Client.GroupManager.SendMessageAsync(group.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "older chat"
            }
        }, Token);
        var older = await ExportAsync(source, BackupSelection.All);
        await source.Client.GroupManager.UpdateGroupAsync(group.Ref, new()
        {
            Name = "current"
        }, Token);
        await target.Client.GroupManager.ImportBackupAsync(Replay(await ExportAsync(source)), Token);
        var notifications = 0;
        target.Client.GroupManager.TimelineChanged += (_, _) => notifications++;
        await target.Client.GroupManager.ImportBackupAsync(Replay(older), Token);
        await using (var db = target.Database.Open())
        {
            Assert.Equal("current", (await db.Groups.SingleAsync(Token)).Name);
            Assert.Equal(2, (await db.Groups.SingleAsync(Token)).Sequence);
            var message = await db.GroupEvents.SingleAsync(value => value.IsMessage, Token);
            db.ConversationReads.Add(new()
            {
                ConversationId = group.Ref.GroupId,
                Sequence = message.Sequence
            });
            await db.SaveChangesAsync(Token);
        }
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(older), Token)).Applied);
        Assert.Equal(1, notifications);
        await using var check = target.Database.Open();
        Assert.Equal(3, await check.GroupEvents.CountAsync(Token));
        Assert.Equal(1, (await check.ConversationReads.SingleAsync(Token)).Sequence);
    }

    [Fact]
    public async Task Normal_sync_can_continue_an_interrupted_import_without_backup_specific_state()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var relay = new GroupRelay(fixture);
        relay.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, new()
        {
            Name = "before",
            MemberCapacity = 10
        }, Token);
        using var peer = new AccountSigner();
        await manager.BanAsync(group.Ref, [peer.AccountId], Token);
        await manager.UpdateGroupAsync(group.Ref, new()
        {
            Name = "after"
        }, Token);
        var records = await ExportAsync(fixture);
        await using (var db = fixture.Database.Open())
        {
            await db.GroupEvents.ExecuteDeleteAsync(Token);
            await db.GroupMembers.ExecuteDeleteAsync(Token);
            await db.GroupBans.ExecuteDeleteAsync(Token);
            await db.Groups.ExecuteDeleteAsync(Token);
        }
        await manager.ImportBackupAsync(Replay(records.Take(2)), Token);
        await fixture.ReopenAsync();
        await fixture.Client.GroupManager.SynchronizeAsync(group.Ref, Token);
        Assert.Equal(0, (await fixture.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied);
        await using var check = fixture.Database.Open();
        Assert.Equal("after", (await check.Groups.SingleAsync(Token)).Name);
        Assert.Single(await check.GroupBans.ToListAsync(Token));
        Assert.Equal(3, await check.GroupEvents.CountAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Merge_restores_missing_plaintext_once_without_replacing_rejected_history(bool rejected)
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
            Name = "restore",
            MemberCapacity = 10
        }, Token);
        await source.Client.GroupManager.SendMessageAsync(group.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "recovered plaintext"
            }
        }, Token);
        var records = await ExportAsync(source, BackupSelection.All);
        var pending = records.Select(record =>
        {
            if (record.GetProperty("type").GetString() != "group_event")
                return record;
            var value = BackupData.Read<GroupEventRecord>(record.GetProperty("data"));
            if (!value.IsMessage)
                return record;
            value.IsMessage = false;
            value.DecryptedPayloadJson = null;
            value.Rejection = rejected ? "locally rejected" : null;
            return BackupData.Record("group_event", BackupData.Project(value, BackupData.EventFields));
        }).ToList();
        await target.Client.GroupManager.ImportBackupAsync(Replay(pending), Token);
        var notifications = new List<GroupMessageInfo>();
        target.Client.GroupManager.TimelineChanged += (_, args) =>
        {
            using var db = target.Database.Open();
            Assert.True(db.GroupEvents.Single(value => value.MessageId != null).IsMessage);
            notifications.AddRange(args.Messages);
        };
        var requests = target.Relay.Requests.Count;
        var report = await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        Assert.Equal(rejected ? 0 : 1, report.Applied);
        Assert.Equal(records.Count, report.Applied + report.Unchanged + report.Conflicts);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(pending), Token)).Applied);
        Assert.Equal(requests, target.Relay.Requests.Count);
        await using var check = target.Database.Open();
        var restored = await check.GroupEvents.SingleAsync(value => value.MessageId != null, Token);
        Assert.Equal(1, (await check.Groups.SingleAsync(Token)).Sequence);
        if (rejected)
        {
            Assert.Null(restored.DecryptedPayloadJson);
            Assert.False(restored.IsMessage);
            Assert.Equal("locally rejected", restored.Rejection);
            Assert.Empty(notifications);
        }
        else
        {
            Assert.NotNull(restored.DecryptedPayloadJson);
            Assert.True(restored.IsMessage);
            Assert.Equal("recovered plaintext", Assert.Single(notifications).Body!.Text);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unchanged")]
    [InlineData("partially_missing")]
    [InlineData("conflict")]
    [InlineData("application_conflict")]
    public async Task Epoch_keys_merge_as_one_record_without_filling_other_epochs(string scenario)
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
            Name = "rotated",
            MemberCapacity = 10
        }, Token);
        relay.Events.Add(new()
        {
            Sequence = 1,
            Epoch = 1,
            AcceptedAt = Clock.UtcNow.ToUnixTimeSeconds(),
            Payload = new GroupKeyRotated()
        });
        await source.Client.GroupManager.SynchronizeAsync(group.Ref, Token);
        var records = await ExportAsync(source);
        var first = await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        Assert.Equal(records.Count, first.Applied + first.Unchanged + first.Conflicts);
        var repeated = await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        Assert.Equal(0, repeated.Applied);
        Assert.Equal(0, repeated.Conflicts);
        Assert.Equal(records.Count, repeated.Unchanged);
        var secretRecord = Assert.Single(records, record => record.GetProperty("type").GetString() == "group_epoch" && record.GetProperty("data").GetProperty("epoch").GetInt64() == 0);
        var secret = BackupData.Read<BackupData.EpochData>(secretRecord.GetProperty("data"));
        Assert.NotNull(secret.ClientSecret);
        Assert.NotNull(secret.ApplicationSecret);
        var purpose = $"Meshline/{TestNetwork.Context}/{target.Account.AccountId}/{target.Client.Device!.GetDeviceId(TestNetwork.Context)}/group/{group.Ref.GroupId}/client/{secret.Commitment}";
        var applicationPurpose = $"Meshline/{TestNetwork.Context}/{target.Account.AccountId}/{target.Client.Device!.GetDeviceId(TestNetwork.Context)}/group/{group.Ref.GroupId}/epoch/0";
        await using (var db = target.Database.Open())
        {
            var epochs = await db.GroupEpochs.OrderBy(value => value.Epoch).ToListAsync(Token);
            Assert.Equal(2, epochs.Count);
            if (scenario != "unchanged")
                epochs[0].ProtectedApplicationSecret = null;
            if (scenario == "missing")
                epochs[0].ProtectedClientSecret = null;
            if (scenario == "conflict")
                epochs[0].ProtectedClientSecret = await target.Protector.ProtectAsync(new byte[32], purpose, Token);
            if (scenario == "application_conflict")
            {
                epochs[0].ProtectedClientSecret = null;
                epochs[0].ProtectedApplicationSecret = await target.Protector.ProtectAsync(new byte[32], applicationPurpose, Token);
            }
            Assert.Null(epochs[1].ProtectedClientSecret);
            Assert.Null(epochs[1].ProtectedApplicationSecret);
            await db.SaveChangesAsync(Token);
        }
        var report = await target.Client.GroupManager.ImportBackupAsync(Replay([secretRecord]), Token);
        Assert.Equal(1, report.Applied + report.Unchanged + report.Conflicts);
        Assert.Equal(scenario is "missing" or "partially_missing" ? 1 : 0, report.Applied);
        Assert.Equal(scenario == "unchanged" ? 1 : 0, report.Unchanged);
        Assert.Equal(scenario is "conflict" or "application_conflict" ? 1 : 0, report.Conflicts);
        await using var check = target.Database.Open();
        var restored = await check.GroupEpochs.OrderBy(value => value.Epoch).ToListAsync(Token);
        if (scenario == "application_conflict")
            Assert.Null(restored[0].ProtectedClientSecret);
        else
            Assert.Equal(scenario == "conflict" ? new byte[32] : secret.ClientSecret,
            await target.Protector.UnprotectAsync(restored[0].ProtectedClientSecret!, purpose, Token));
        Assert.Equal(scenario == "conflict", restored[0].ProtectedApplicationSecret is null);
        if (restored[0].ProtectedApplicationSecret is { } application)
            Assert.Equal(scenario == "application_conflict" ? new byte[32] : secret.ApplicationSecret,
                await target.Protector.UnprotectAsync(application, applicationPurpose, Token));
        Assert.Null(restored[1].ProtectedClientSecret);
        Assert.Null(restored[1].ProtectedApplicationSecret);
    }

    static async Task<List<JsonElement>> ExportAsync(TestClient fixture, BackupSelection? history = null)
    {
        var result = new List<JsonElement>();
        await foreach (var record in fixture.Client.GroupManager.ExportBackupAsync(history, Token))
            result.Add(record);
        return result;
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
        {
            Token.ThrowIfCancellationRequested();
            yield return record;
        }
        await Task.CompletedTask;
    }
}
