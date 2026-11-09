using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Tests.Components.Conversations;

public sealed class BackupReadStateTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "read position backup";
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_import_uses_target_local_positions_without_copying_or_advancing_read_progress(bool hasReadPosition)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        var rows = Enumerable.Range(0, 4).Select(_ => Message(source, peer.AccountId)).ToArray();
        await using (var db = source.Database.Open())
        {
            db.Messages.AddRange(rows);
            var sent = Message(source, source.Account.AccountId);
            sent.Recipient = peer.AccountId;
            db.Messages.Add(sent);
            await db.SaveChangesAsync(Token);
        }
        // The source has read everything; only the target's cursor may affect imported history.
        await source.Client.MarkReadAsync(peer.AccountId, Token);
        await using (var db = target.Database.Open())
        {
            foreach (var row in rows.Take(3))
            {
                row.LocalSequence = 0;
                db.Messages.Add(row);
            }
            await db.SaveChangesAsync(Token);
        }
        if (hasReadPosition)
            await target.Client.MarkReadAsync(peer.AccountId, rows[1].LocalSequence, Token);
        var cursor = hasReadPosition ? rows[1].LocalSequence : (long?)null;
        var expected = hasReadPosition ? 2 : 4;
        using var backup = new MemoryStream();
        await source.Client.ExportBackupAsync(backup, Password, new()
        {
            DirectHistory = BackupSelection.All
        }, Token);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            backup.Position = 0;
            var report = await target.Client.ImportBackupAsync(backup, Password, Token);
            Assert.Equal(attempt == 0 ? 2 : 0, report.Applied);
            Assert.Equal(expected, (await target.Client.GetConversationAsync(peer.AccountId, Token))!.UnreadCount);
            await using var db = target.Database.Open();
            Assert.Equal(cursor, (await db.ConversationReads.SingleOrDefaultAsync(Token))?.Sequence);
            Assert.Equal(5, await db.Messages.CountAsync(Token));
            foreach (var row in rows.Take(3))
                Assert.Equal(row.LocalSequence, (await db.Messages.SingleAsync(value => value.MessageId == row.MessageId, Token)).LocalSequence);
        }
        await target.ReopenAsync();
        Assert.Equal(expected, (await target.Client.GetConversationAsync(peer.AccountId, Token))!.UnreadCount);
        await target.Client.MarkReadAsync(peer.AccountId, Token);
        Assert.Equal(0, (await target.Client.GetConversationAsync(peer.AccountId, Token))!.UnreadCount);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(22L, 1)]
    [InlineData(23L, 0)]
    [InlineData(24L, 0)]
    public async Task Group_import_obeys_before_equal_after_and_absent_target_read_positions(long? readPosition, long unread)
    {
        using var time = Clock.Use(new ManualClock());
        await using var target = new TestClient(new AccountSigner(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
        await target.InitializeAsync();
        using var backupVector = await BackupFormatSetup.ReadVectorAsync(Token);
        var records = backupVector.RootElement.GetProperty("group_restore").GetProperty("records").EnumerateArray().ToArray();
        await target.Client.GroupManager.ImportBackupAsync(Replay(records), Token);
        var chain = ProtocolVectors.Read("groups").GetProperty("management_chain");
        var entry = ProtocolModel.FromJson<GroupEvent>(chain.GetProperty("sync_response").GetProperty("events").EnumerateArray()
            .Single(value => value.GetProperty("sequence").GetInt64() == 23).GetRawText())!;
        var envelope = Assert.IsType<GroupMessageEnvelope>(entry.Payload);
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(chain.GetProperty("actors").EnumerateArray()
            .Single(value => value.GetProperty("device_id").GetString() == entry.SignerDeviceId).GetProperty("certificate").GetRawText())!;
        Assert.NotEqual(target.Account.AccountId, certificate.Account);
        // Supply already-decoded local chat for the signed historical envelope.
        var message = new GroupEventRecord
        {
            GroupId = envelope.GroupId,
            Sequence = entry.Sequence,
            Epoch = entry.Epoch,
            PayloadJson = envelope.ToJson(),
            CertificateJson = certificate.ToJson(),
            MessageId = envelope.MessageId,
            Sender = certificate.Account,
            SenderDeviceId = entry.SignerDeviceId,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(envelope.CreatedAt),
            IsMessage = true,
            DecryptedPayloadJson = new GroupMessage { Body = new() { ContentType = "text/plain", Text = "restored history" } }.ToJson()
        };
        if (readPosition is { } position)
        {
            await using var db = target.Database.Open();
            db.ConversationReads.Add(new()
            {
                ConversationId = envelope.GroupId,
                Sequence = position
            });
            await db.SaveChangesAsync(Token);
        }
        var record = BackupData.Record("group_event", BackupData.Project(message, BackupData.EventFields));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(attempt == 0 ? 1 : 0, (await target.Client.GroupManager.ImportBackupAsync(Replay([record]), Token)).Applied);
            Assert.Equal(unread, (await target.Client.GetConversationAsync(envelope.GroupId, Token))!.UnreadCount);
            await using var db = target.Database.Open();
            Assert.Equal(readPosition, (await db.ConversationReads.SingleOrDefaultAsync(Token))?.Sequence);
        }
        await target.ReopenAsync();
        Assert.Equal(unread, (await target.Client.GetConversationAsync(envelope.GroupId, Token))!.UnreadCount);
        await target.Client.MarkReadAsync(envelope.GroupId, Token);
        Assert.Equal(0, (await target.Client.GetConversationAsync(envelope.GroupId, Token))!.UnreadCount);
    }

    static StoredMessageRecord Message(TestClient fixture, string sender) => new()
    {
        Sender = sender,
        Recipient = fixture.Account.AccountId,
        SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
        MessageId = Identifiers.CreateMessageId(),
        CreatedAt = Clock.UtcNow,
        IsDirect = true,
        PayloadType = "meshline.message.direct",
        PayloadJson = new DirectMessage { Body = new() { ContentType = "text/plain", Text = "history" } }.ToJson()
    };

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
            yield return record;
        await Task.CompletedTask;
    }
}
