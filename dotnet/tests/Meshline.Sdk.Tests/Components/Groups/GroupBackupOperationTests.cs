using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupBackupOperationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Imported_leave_confirms_pending_operation_before_a_later_rejoin_and_restart()
    {
        using var time = Clock.Use(new ManualClock());
        using var vector = await BackupFormatSetup.ReadVectorAsync(Token);
        await using var fixture = new TestClient(new AccountSigner(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
        await fixture.InitializeAsync();
        var records = vector.RootElement.GetProperty("group_restore").GetProperty("records").EnumerateArray()
            .Where(record => record.GetProperty("type").GetString() != "group_event" || record.GetProperty("data").GetProperty("sequence").GetInt64() <= 20).ToArray();
        var leave = BackupData.Read<GroupEventRecord>(records[^1].GetProperty("data"));
        Assert.IsType<GroupMemberLeave>(ProtocolModel.FromJson<TypedProtocolModel>(leave.PayloadJson));
        await fixture.Client.GroupManager.ImportBackupAsync(Replay(records[..^1]), Token);
        await using (var db = fixture.Database.Open())
        {
            Assert.Equal(GroupMembershipState.Member, (await db.Groups.SingleAsync(Token)).Membership);
            // The relay accepted this request, but the submitting device lost the response.
            db.GroupOperations.Add(new()
            {
                GroupId = leave.GroupId,
                Method = "group.member.leave",
                RequestJson = leave.PayloadJson
            });
            await db.SaveChangesAsync(Token);
        }

        // The new owner admits this account again after its earlier leave was accepted.
        using var owner = new AccountSigner(Enumerable.Range(2, 32).Select(value => (byte)value).ToArray());
        var signer = new DeviceSigner(owner, fixture.Relay.Clock);
        var create = ProtocolModel.FromJson<GroupCreate>(records[1].GetProperty("data").GetProperty("payload_json").GetString()!)!;
        var joined = new GroupApplicationApproval
        {
            GroupId = leave.GroupId,
            PrevHash = "sha256:" + Base64Url.EncodeToString(SHA256.HashData(ProtocolModel.FromJson<TypedProtocolModel>(leave.PayloadJson)!.GetSigningInput(TestNetwork.Context))),
            Members = [create.Owner],
            DeviceSignature = []
        };
        joined = joined with
        {
            DeviceSignature = [.. await signer.SignAsync(joined.GetSigningInput(TestNetwork.Context), Token)]
        };
        var rejoin = new GroupEventRecord
        {
            GroupId = leave.GroupId,
            Sequence = leave.Sequence + 1,
            Epoch = leave.Epoch + 1,
            PayloadJson = joined.ToJson(),
            CertificateJson = signer.Certificate.ToJson()
        };
        var requests = fixture.Relay.Requests.Count;
        await fixture.Client.GroupManager.ImportBackupAsync(Replay([.. records,
            BackupData.Record("group_event", BackupData.Project(rejoin, BackupData.EventFields))]), Token);
        Assert.Equal(requests, fixture.Relay.Requests.Count);
        await using (var db = fixture.Database.Open())
        {
            Assert.Empty(await db.GroupOperations.ToListAsync(Token));
            var restored = await db.Groups.SingleAsync(Token);
            Assert.Equal(GroupMembershipState.Member, restored.Membership);
            Assert.Equal(GroupRole.Member, restored.Role);
            Assert.Equal(rejoin.Sequence, restored.Sequence);
        }

        await fixture.ReopenAsync();
        await fixture.Client.GroupManager.StartAsync(Token);
        // The hosting relay is offline, but background recovery must still keep this
        // group eligible for synchronization after the restored rejoin.
        await AsyncTest.UntilAsync(async () => (await fixture.Client.GroupManager.GetSyncStatusAsync(leave.GroupId, Token)).State == ResourceSyncState.Blocked);
        await fixture.Client.GroupManager.StopAsync(Token);
        await using var check = fixture.Database.Open();
        Assert.NotNull(await check.GroupMembers.FindAsync([leave.GroupId, fixture.Account.AccountId], Token));
        Assert.Empty(await check.GroupOperations.ToListAsync(Token));
        var current = await check.Groups.SingleAsync(Token);
        Assert.Equal(rejoin.Sequence, current.Sequence);
        Assert.Equal(GroupMembershipState.Member, current.Membership);
        Assert.Equal(GroupRole.Member, current.Role);
    }

    [Theory]
    [InlineData(2, "group.application.approve")]
    [InlineData(6, "group.member.recovery.approve")]
    [InlineData(8, "group.secret.rotation.commit")]
    [InlineData(24, "group.close")]
    public async Task New_events_confirm_matching_operations_and_notify_once(long sequence, string method)
    {
        using var time = Clock.Use(new ManualClock());
        using var vector = await BackupFormatSetup.ReadVectorAsync(Token);
        var records = vector.RootElement.GetProperty("group_restore").GetProperty("records").EnumerateArray().ToArray();
        var index = Array.FindIndex(records, record => record.GetProperty("type").GetString() == "group_event" && record.GetProperty("data").GetProperty("sequence").GetInt64() == sequence);
        var entry = BackupData.Read<GroupEventRecord>(records[index].GetProperty("data"));
        var payload = ProtocolModel.FromJson<TypedProtocolModel>(entry.PayloadJson)!;
        await using var fixture = new TestClient(new AccountSigner(Enumerable.Range(sequence == 24 ? 2 : 1, 32).Select(value => (byte)value).ToArray()));
        await fixture.InitializeAsync();
        await fixture.Client.GroupManager.ImportBackupAsync(Replay(records[..index]), Token);
        await using (var db = fixture.Database.Open())
        {
            var group = await db.Groups.SingleAsync(Token);
            // Request-only encrypted boxes do not participate in timeline matching.
            ProtocolModel request = payload switch
            {
                GroupApplicationApproval approval => new GroupApplicationApproveRequest
                {
                    Approval = approval,
                    ClientSecretCommitment = group.Commitment!,
                    ClientSecretBoxes = ImmutableDictionary<string, GroupSecretBox>.Empty
                },
                GroupMemberRecoveryApproval approval => new GroupRecoveryApproveRequest
                {
                    Approval = approval,
                    ClientSecretCommitment = group.Commitment!,
                    ClientSecretBoxes = ImmutableDictionary<string, GroupSecretBox>.Empty
                },
                _ => payload
            };
            db.GroupOperations.Add(new()
            {
                GroupId = entry.GroupId,
                Method = method,
                RequestJson = request.ToJson()
            });
            // A different pending request must survive importing this event.
            db.GroupOperations.Add(new()
            {
                GroupId = entry.GroupId,
                Method = "group.member.leave",
                RequestJson = records[^2].GetProperty("data").GetProperty("payload_json").GetString()
            });
            if (method == "group.secret.rotation.commit")
                db.GroupRotations.Add(new()
                {
                    GroupId = entry.GroupId,
                    BaseCommitment = group.Commitment!,
                    Commitment = ((GroupSecretRotation)payload).ClientSecretCommitment,
                    ProtectedSecret = new byte[32]
                });
            await db.SaveChangesAsync(Token);
        }
        var applications = 0;
        var recoveries = 0;
        var changes = 0;
        fixture.Client.GroupManager.ApplicationsChanged += (_, _) => applications++;
        fixture.Client.GroupManager.KeyRecoveryChanged += (_, _) => recoveries++;
        fixture.Client.GroupManager.GroupChanged += (_, _) => changes++;
        var requests = fixture.Relay.Requests.Count;
        Assert.Equal(1, (await fixture.Client.GroupManager.ImportBackupAsync(Replay([records[index]]), Token)).Applied);
        Assert.Equal(1, (await fixture.Client.GroupManager.ImportBackupAsync(Replay([records[index]]), Token)).Unchanged);
        Assert.Equal(requests, fixture.Relay.Requests.Count);
        Assert.Equal(method == "group.application.approve" ? 1 : 0, applications);
        Assert.Equal(method == "group.member.recovery.approve" ? 1 : 0, recoveries);
        Assert.Equal(1, changes);
        await using var check = fixture.Database.Open();
        Assert.Equal("group.member.leave", (await check.GroupOperations.SingleAsync(Token)).Method);
        Assert.Empty(await check.GroupRotations.ToListAsync(Token));
        Assert.Equal(sequence, (await check.Groups.SingleAsync(Token)).Sequence);
        Assert.Equal(method == "group.close", (await check.Groups.SingleAsync(Token)).LocallyClosed);
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
