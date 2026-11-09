using Meshline.Backups;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Tests.Backups;

public sealed class GroupBackupSizeTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Large_management_events_roundtrip_with_default_history_selection(bool escaped)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await source.InitializeAsync();
        await using var target = Create();
        await target.InitializeAsync();
        var relay = new GroupRelay(source);
        relay.Install();
        var group = await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId,
            new()
            {
                Name = "large management backup",
                MemberCapacity = 10
            }, Token);
        GroupManagementOperation payload;
        if (escaped)
        {
            await using var database = source.Database.Open();
            var state = await database.Groups.SingleAsync(Token);
            var update = new Meshline.Models.Protocol.GroupUpdate
            {
                GroupId = group.Ref.GroupId,
                PrevHash = state.ManagementHash!,
                Name = "updated",
                DeviceSignature = [.. new byte[64]],
                AdditionalProperties = ImmutableDictionary<string, JsonElement>.Empty.Add("extra", JsonSerializer.SerializeToElement(""))
            };
            var remaining = 1_048_576 - Encoding.UTF8.GetByteCount(update.ToJson());
            var extra = new string('\\', remaining / 2) + (remaining % 2 == 1 ? "x" : "");
            update = update with
            {
                AdditionalProperties = update.AdditionalProperties.SetItem("extra", JsonSerializer.SerializeToElement(extra))
            };
            update = update with
            {
                DeviceSignature = [.. await source.Client.DeviceManager.SignAsync(update.GetSigningInput(TestNetwork.Context), Token)]
            };
            Assert.Equal(1_048_576, Encoding.UTF8.GetByteCount(update.ToJson()));
            relay.Events.Add(new()
            {
                Sequence = 1,
                Epoch = 0,
                Payload = update,
                AcceptedAt = Clock.UtcNow.ToUnixTimeSeconds(),
                SignerDeviceId = source.Client.Device!.GetDeviceId(TestNetwork.Context)
            });
            await source.Client.GroupManager.SynchronizeAsync(group.Ref, Token);
            payload = update;
        }
        else
        {
            var accounts = Enumerable.Range(1, 20_000).Select(Account).ToArray();
            await source.Client.GroupManager.BanAsync(group.Ref, accounts, Token);
            payload = Assert.IsType<GroupMemberBan>(relay.Events[^1].Payload);
            Assert.InRange(Encoding.UTF8.GetByteCount(payload.ToJson()), 1_000_000, 1_048_576);
        }
        Assert.Null(payload.Validate(TestNetwork.Context));
        var records = await source.Client.GroupManager.ExportBackupAsync(cancellationToken: Token).ToListAsync(Token);
        var largest = records.Max(record => JsonSerializer.SerializeToUtf8Bytes(record, BackupCodec.Json).Length);
        Assert.InRange(largest, (escaped ? 2 : 1) * 1_048_576 + 1, 4_194_304);

        using var backup = new MemoryStream();
        await source.Client.ExportBackupAsync(backup, "large management", cancellationToken: Token);
        backup.Position = 0;
        Assert.Equal(0, (await target.Client.ImportBackupAsync(backup, "large management", Token)).Conflicts);
        await using var restored = target.Database.Open();
        var entry = await restored.GroupEvents.SingleAsync(value => value.Sequence == 1, Token);
        Assert.Equal(payload.ToJson(), entry.PayloadJson);
        var restoredGroup = await restored.Groups.SingleAsync(Token);
        Assert.Equal(1, restoredGroup.Sequence);
        Assert.Equal(escaped ? "updated" : "large management backup", restoredGroup.Name);
        Assert.Equal(escaped ? 0 : 20_000, await restored.GroupBans.CountAsync(Token));
    }

    static string Account(int id)
    {
        Span<byte> payload = stackalloc byte[25];
        payload.Clear();
        payload[0] = 0x35;
        BinaryPrimitives.WriteInt32LittleEndian(payload[1..], id);
        SHA256.HashData(SHA256.HashData(payload[..21])).AsSpan(0, 4).CopyTo(payload[21..]);
        var number = new BigInteger(payload, isUnsigned: true, isBigEndian: true);
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        Span<char> address = stackalloc char[35];
        var offset = address.Length;
        while (number > 0)
        {
            number = BigInteger.DivRem(number, 58, out var digit);
            address[--offset] = alphabet[(int)digit];
        }
        return "neo:860833102:" + new string(address[offset..]);
    }
}
