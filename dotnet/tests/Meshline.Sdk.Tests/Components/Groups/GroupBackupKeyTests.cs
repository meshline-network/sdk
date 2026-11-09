using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupBackupKeyTests
{
    const string Password = "backup test password \U0001F510";
    static readonly BackupOptions WithHistory = new() { DirectHistory = BackupSelection.All, GroupHistory = BackupSelection.All };
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Group_secret_conflicts_preserve_local_keys_and_do_not_enable_conflicting_epoch_material(bool evidenceConflict)
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
        var device = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        await fixture.Client.StopAsync(Token);
        using var backup = new MemoryStream();
        await fixture.Client.ExportBackupAsync(backup, Password, WithHistory, cancellationToken: Token);
        var different = await fixture.Protector.ProtectAsync(new byte[32], $"Meshline/{TestNetwork.Context}/{fixture.Account.AccountId}/{device}/group/{group.Ref.GroupId}/epoch/0", Token);
        await using (var db = fixture.Database.Open())
        {
            var epoch = await db.GroupEpochs.SingleAsync(Token);
            if (evidenceConflict)
            {
                epoch.Commitment = "sha256:" + System.Buffers.Text.Base64Url.EncodeToString(new byte[32]);
                epoch.ProtectedClientSecret = null;
                epoch.ProtectedApplicationSecret = null;
            }
            else
                epoch.ProtectedApplicationSecret = different;
            await db.SaveChangesAsync(Token);
        }
        backup.Position = 0;
        var report = await fixture.Client.ImportBackupAsync(backup, Password, Token);
        Assert.True(report.Conflicts > 0);
        await using var check = fixture.Database.Open();
        var retained = await check.GroupEpochs.SingleAsync(Token);
        if (evidenceConflict)
        {
            Assert.Null(retained.ProtectedApplicationSecret);
            Assert.Null(retained.ProtectedClientSecret);
        }
        else
            Assert.Equal(different, retained.ProtectedApplicationSecret);
    }

    [Fact]
    public async Task Historical_keys_without_a_discovered_group_resume_after_protector_failure()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        const string groupId = "grp_AAAAAAAAAAAAAAAAAAAAAA";
        var sourceDevice = source.Client.Device!.GetDeviceId(TestNetwork.Context);
        var targetDevice = target.Client.Device!.GetDeviceId(TestNetwork.Context);
        var secrets = new[] { RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32) };
        string Purpose(string device, int epoch) => $"Meshline/{TestNetwork.Context}/{source.Account.AccountId}/{device}/group/{groupId}/epoch/{epoch}";
        await using (var db = source.Database.Open())
        {
            for (var epoch = 0; epoch < 2; epoch++)
                db.GroupEpochs.Add(new()
                {
                    GroupId = groupId,
                    Epoch = epoch,
                    ProtectedApplicationSecret = await source.Protector.ProtectAsync(secrets[epoch], Purpose(sourceDevice, epoch), Token)
                });
            await db.SaveChangesAsync(Token);
        }
        await source.Client.StopAsync(Token);
        await target.Client.StopAsync(Token);
        using var backup = new MemoryStream();
        await source.Client.ExportBackupAsync(backup, Password, new(), Token);
        target.Protector.Fail = true;
        backup.Position = 0;
        await Assert.ThrowsAsync<IOException>(() => target.Client.ImportBackupAsync(backup, Password, Token));
        await using (var check = target.Database.Open())
        {
            Assert.Empty(await check.GroupEpochs.ToListAsync(Token));
        }
        target.Protector.Fail = false;
        backup.Position = 0;
        await target.Client.ImportBackupAsync(backup, Password, Token);
        await using var restored = target.Database.Open();
        Assert.Empty(await restored.Groups.ToListAsync(Token));
        foreach (var epoch in await restored.GroupEpochs.ToListAsync(Token))
        {
            Assert.Null(epoch.Commitment);
            Assert.Equal(secrets[epoch.Epoch], await target.Protector.UnprotectAsync(epoch.ProtectedApplicationSecret!, Purpose(targetDevice, (int)epoch.Epoch), Token));
        }
    }

    [Fact]
    public async Task Fixed_epoch_record_restores_both_secrets_and_roundtrips_as_one_record()
    {
        using var time = Clock.Use(new ManualClock());
        using var vector = await BackupFormatSetup.ReadVectorAsync(Token);
        var record = vector.RootElement.GetProperty("epoch_restore").GetProperty("record");
        var value = BackupData.Read<BackupData.EpochData>(record.GetProperty("data"));
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var manager = fixture.Client.GroupManager;
        Assert.Equal(1, (await manager.ImportBackupAsync(Replay([record]), Token)).Applied);
        Assert.Equal(1, (await manager.ImportBackupAsync(Replay([record]), Token)).Unchanged);
        await using (var db = fixture.Database.Open())
        {
            var epoch = await db.GroupEpochs.SingleAsync(Token);
            Assert.Equal(value.Epoch, epoch.Epoch);
            Assert.Equal(value.Commitment, epoch.Commitment);
            Assert.Equal(value.MemberPublicKey, epoch.MemberPublicKey);
            var prefix = $"Meshline/{TestNetwork.Context}/{fixture.Account.AccountId}/{fixture.Client.Device!.GetDeviceId(TestNetwork.Context)}/group/{value.GroupId}";
            Assert.Equal(value.ClientSecret, await fixture.Protector.UnprotectAsync(epoch.ProtectedClientSecret!, $"{prefix}/client/{value.Commitment}", Token));
            Assert.Equal(value.ApplicationSecret, await fixture.Protector.UnprotectAsync(epoch.ProtectedApplicationSecret!, $"{prefix}/epoch/{value.Epoch}", Token));
        }
        var exported = new List<JsonElement>();
        await foreach (var item in manager.ExportBackupAsync(cancellationToken: Token))
            exported.Add(item);
        Assert.True(JsonElement.DeepEquals(record, Assert.Single(exported)));
    }

    [Theory]
    [InlineData("commitment")]
    [InlineData("member_key")]
    [InlineData("application_secret")]
    [InlineData("application_secret_without_evidence")]
    public async Task Conflicting_epoch_material_preserves_the_complete_local_record(string conflict)
    {
        using var time = Clock.Use(new ManualClock());
        using var vector = await BackupFormatSetup.ReadVectorAsync(Token);
        var record = vector.RootElement.GetProperty("epoch_restore").GetProperty("record");
        var value = BackupData.Read<BackupData.EpochData>(record.GetProperty("data"));
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var manager = fixture.Client.GroupManager;
        await manager.ImportBackupAsync(Replay([record]), Token);
        var differentMemberKey = value.MemberPublicKey!.ToArray();
        differentMemberKey[0] ^= 1;
        var differentSecret = value.ApplicationSecret!.ToArray();
        differentSecret[0] ^= 1;
        var incoming = conflict switch
        {
            "commitment" => value with { Commitment = "sha256:" + System.Buffers.Text.Base64Url.EncodeToString(new byte[32]), ClientSecret = null },
            "member_key" => value with { MemberPublicKey = differentMemberKey },
            "application_secret" => value with { ApplicationSecret = differentSecret },
            _ => value with { Commitment = null, MemberPublicKey = null, ClientSecret = null, ApplicationSecret = differentSecret }
        };
        var report = await manager.ImportBackupAsync(Replay([BackupData.Record("group_epoch", incoming)]), Token);
        Assert.Equal(1, report.Conflicts);
        Assert.Equal(0, report.Applied);
        var exported = await manager.ExportBackupAsync(cancellationToken: Token).ToListAsync(Token);
        Assert.True(JsonElement.DeepEquals(record, Assert.Single(exported)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_secret_without_epoch_evidence_restores_offline_messages_in_either_import_order(bool secretFirst)
    {
        using var time = Clock.Use(new ManualClock());
        await using var origin = Create();
        await origin.InitializeAsync();
        await using var source = Create();
        await source.InitializeAsync();
        await using var target = Create();
        await target.InitializeAsync();
        var relay = new GroupRelay(origin);
        relay.Install();
        var group = await origin.Client.GroupManager.CreateGroupAsync(origin.Relay.RelayId,
            new()
            {
                Name = "historical key",
                MemberCapacity = 10
            }, Token);
        await origin.Client.GroupManager.SendMessageAsync(group.Ref,
            new()
            {
                Body = new()
                {
                    ContentType = "text/plain",
                    Text = "restored offline"
                }
            }, Token);
        var original = await origin.Client.GroupManager.ExportBackupAsync(BackupSelection.All, Token).ToListAsync(Token);
        var epoch = BackupData.Read<BackupData.EpochData>(Assert.Single(original,
            record => record.GetProperty("type").GetString() == "group_epoch").GetProperty("data"));
        Assert.NotNull(epoch.ApplicationSecret);

        // Ordinary account synchronization can receive history secrets before the group is discovered.
        var history = new AccountGroupHistorySecretSync
        {
            Secrets = [new() { GroupId = group.Ref.GroupId, Epoch = 0, ApplicationSecret = [.. epoch.ApplicationSecret] }]
        };
        await source.Client.MessageManager.SendPayloadAsync(source.Account.AccountId, history, Token);
        MessageSendRequest request;
        await using (var db = source.Database.Open())
            request = ProtocolModel.FromJson<MessageSendRequest>((await db.MessageOutbox.SingleAsync(Token)).RequestJson)!;
        source.Relay.Handler = (http, _) => Task.FromResult(http.Method switch
        {
            "message.timeline.sync" => OfflineRelay.Json(new MessageTimelinePage
            {
                Items = long.Parse(RequestQuery.Parse(http)["after"]) < 1
                    ? [new() { Sequence = 1, AcceptedAt = request.Envelope.CreatedAt, Envelope = request.Envelope, KeyBox = request.RecipientBoxes[0] }] : [],
                Certificates = [source.Client.Device!],
                HasMore = false
            }),
            "group.sync" => OfflineRelay.Error("forbidden"),
            _ => source.Relay.Respond(http)
        });
        await source.Client.MessageManager.SynchronizeAsync(source.Relay.RelayId, Token);
        await Assert.ThrowsAsync<RelayException>(() => source.Client.GroupManager.SynchronizeAsync(
            new GroupRef { GroupId = group.Ref.GroupId, RelayId = source.Relay.RelayId }, Token));
        await using (var db = source.Database.Open())
        {
            Assert.Empty(await db.Groups.ToListAsync(Token));
            var received = await db.GroupEpochs.SingleAsync(Token);
            Assert.Null(received.Commitment);
            Assert.Null(received.MemberPublicKey);
            Assert.NotNull(received.ProtectedApplicationSecret);
        }
        using var backup = new MemoryStream();
        await source.Client.ExportBackupAsync(backup, "history only", cancellationToken: Token);
        var timeline = original.Where(record => record.GetProperty("type").GetString() is "group" or "group_event")
            .Select(record =>
            {
                if (record.GetProperty("type").GetString() != "group_event")
                    return record;
                var entry = BackupData.Read<GroupEventRecord>(record.GetProperty("data"));
                entry.DecryptedPayloadJson = null;
                entry.IsMessage = false;
                return BackupData.Record("group_event", BackupData.Project(entry, BackupData.EventFields));
            }).ToArray();
        if (!secretFirst)
            Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(timeline), Token)).Conflicts);
        backup.Position = 0;
        Assert.Equal(0, (await target.Client.ImportBackupAsync(backup, "history only", Token)).Conflicts);
        if (secretFirst)
            Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(timeline), Token)).Conflicts);
        await using (var db = target.Database.Open())
        {
            var restored = await db.GroupEpochs.SingleAsync(Token);
            Assert.Equal(epoch.Commitment, restored.Commitment);
            Assert.Equal(epoch.MemberPublicKey, restored.MemberPublicKey);
            Assert.NotNull(restored.ProtectedApplicationSecret);
        }
        backup.Position = 0;
        var repeated = await target.Client.ImportBackupAsync(backup, "history only", Token);
        Assert.Equal(0, repeated.Applied);
        Assert.Equal(0, repeated.Conflicts);

        // The target has no hosting relay or member private key; only the imported history secret can decrypt.
        target.Relay.Handler = (http, _) => Task.FromResult(http.Method.StartsWith("group.", StringComparison.Ordinal)
            ? OfflineRelay.Error("forbidden") : target.Relay.Respond(http));
        var readable = AsyncTest.Signal();
        target.Client.GroupManager.TimelineChanged += (_, _) => readable.TrySetResult();
        await target.Client.GroupManager.StartAsync(Token);
        await readable.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await target.Client.GroupManager.StopAsync(Token);
        await using var check = target.Database.Open();
        var message = await check.GroupEvents.SingleAsync(value => value.IsMessage, Token);
        Assert.Equal("restored offline", ProtocolModel.FromJson<GroupMessage>(message.DecryptedPayloadJson!)!.Body!.Text);
    }

    [Theory]
    [InlineData("protection")]
    [InlineData("storage")]
    [InlineData("cancellation")]
    public async Task Epoch_import_commits_the_pair_or_neither_and_keeps_previous_epochs(string failure)
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
            Name = "atomic keys",
            MemberCapacity = 10
        }, Token);
        await AdvanceEpochAsync(source, relay, group.Ref);
        var records = new List<JsonElement>();
        await foreach (var record in source.Client.GroupManager.ExportBackupAsync(cancellationToken: Token))
            records.Add(record);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var clientProtected = false;
        target.Protector.WriteFailure = purpose =>
        {
            if (purpose.Contains("/client/", StringComparison.Ordinal))
                clientProtected = true;
            if (purpose.EndsWith("/epoch/0", StringComparison.Ordinal))
                clientProtected = false;
            if (!purpose.EndsWith("/epoch/1", StringComparison.Ordinal))
                return null;
            Assert.True(clientProtected);
            if (failure == "protection")
                return new IOException("fail second secret");
            if (failure != "cancellation")
                return null;
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        };
        if (failure == "storage")
        {
            await using var db = target.Database.Open();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_epoch BEFORE UPDATE OF ProtectedApplicationSecret ON GroupEpochs WHEN NEW.Epoch = 1 AND NEW.ProtectedApplicationSecret IS NOT NULL BEGIN SELECT RAISE(ABORT, 'fail epoch pair'); END;", Token);
        }
        var import = target.Client.GroupManager.ImportBackupAsync(Replay(records), cancellation.Token);
        if (failure == "protection")
            await Assert.ThrowsAsync<IOException>(() => import);
        else if (failure == "storage")
            await Assert.ThrowsAsync<DbUpdateException>(() => import);
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        await using (var db = target.Database.Open())
        {
            var epochs = await db.GroupEpochs.OrderBy(value => value.Epoch).ToListAsync(Token);
            Assert.Equal(2, epochs.Count);
            Assert.NotNull(epochs[0].ProtectedClientSecret);
            Assert.NotNull(epochs[0].ProtectedApplicationSecret);
            Assert.Null(epochs[1].ProtectedClientSecret);
            Assert.Null(epochs[1].ProtectedApplicationSecret);
            if (failure == "storage")
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_epoch;", Token);
        }
        target.Protector.WriteFailure = null;
        await target.ReopenAsync();
        var retry = await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        Assert.True(retry.Applied > 0);
        Assert.Equal(0, retry.Conflicts);
        Assert.Equal(0, (await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token)).Applied);
        await using var check = target.Database.Open();
        Assert.All(await check.GroupEpochs.ToListAsync(Token), epoch =>
        {
            Assert.NotNull(epoch.ProtectedClientSecret);
            Assert.NotNull(epoch.ProtectedApplicationSecret);
        });
    }

    [Fact]
    public async Task Complete_older_backup_does_not_prevent_newer_epoch_recovery()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var relay = new GroupRelay(fixture);
        relay.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, new()
        {
            Name = "older backup",
            MemberCapacity = 10
        }, Token);
        using var backup = new MemoryStream();
        await fixture.Client.ExportBackupAsync(backup, "epoch backup", cancellationToken: Token);
        await AdvanceEpochAsync(fixture, relay, group.Ref);
        await manager.SendMessageAsync(group.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "new epoch message"
            }
        }, Token);
        await using (var db = fixture.Database.Open())
        {
            foreach (var epoch in await db.GroupEpochs.ToListAsync(Token))
            {
                epoch.ProtectedClientSecret = null;
                epoch.ProtectedApplicationSecret = null;
            }
            var message = await db.GroupEvents.SingleAsync(row => row.MessageId != null, Token);
            message.DecryptedPayloadJson = null;
            message.IsMessage = false;
            await db.SaveChangesAsync(Token);
        }
        backup.Position = 0;
        Assert.Equal(0, (await fixture.Client.ImportBackupAsync(backup, "epoch backup", Token)).Conflicts);
        await using (var db = fixture.Database.Open())
        {
            var newer = await db.GroupEpochs.SingleAsync(row => row.Epoch == 1, Token);
            Assert.Null(newer.ProtectedClientSecret);
            Assert.Null(newer.ProtectedApplicationSecret);
        }
        await fixture.ReopenAsync();
        await fixture.Client.GroupManager.SynchronizeAsync(group.Ref, Token);
        await using var verify = fixture.Database.Open();
        Assert.NotNull((await verify.GroupEpochs.SingleAsync(row => row.Epoch == 1, Token)).ProtectedApplicationSecret);
        Assert.NotNull((await verify.GroupEvents.SingleAsync(row => row.MessageId != null, Token)).DecryptedPayloadJson);
    }

    static async Task AdvanceEpochAsync(TestClient fixture, GroupRelay relay, GroupRef group)
    {
        using var peer = new AccountSigner();
        var device = fixture.Relay.AddPeer(peer);
        var invite = await fixture.Client.GroupManager.CreateInviteAsync(group, peer.AccountId, fixture.Relay.Clock.GetUtcNow().AddHours(1), Token);
        await relay.AddApplicationAsync(device, invite.Document, Token);
        await fixture.Client.GroupManager.ApproveApplicationsAsync(group, [peer.AccountId], Token);
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
            yield return record;
        await Task.CompletedTask;
    }
}
