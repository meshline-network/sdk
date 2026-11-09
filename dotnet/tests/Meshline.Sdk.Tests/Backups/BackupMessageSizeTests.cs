using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Meshline.Tests.Backups;

public sealed class BackupMessageSizeTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData("apostrophe", false)]
    [InlineData("apostrophe", true)]
    [InlineData("html", false)]
    [InlineData("html", true)]
    [InlineData("quotes", false)]
    [InlineData("quotes", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("emoji", false)]
    [InlineData("emoji", true)]
    [InlineData("unicode", false)]
    [InlineData("unicode", true)]
    [InlineData("controls", false)]
    [InlineData("controls", true)]
    public async Task Messages_near_the_wire_size_limit_roundtrip_through_encrypted_backup(string kind, bool group)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await source.InitializeAsync();
        await using var target = Create();
        await target.InitializeAsync();
        // Each body occupies approximately 195 KB of canonical protocol JSON, below the envelope limit.
        var (pattern, count) = kind switch
        {
            "apostrophe" => ("'", 195_000),
            "html" => ("<>&", 65_000),
            "quotes" => ("\"\\", 48_750),
            "delete" => ("\u007f", 195_000),
            "emoji" => ("😀", 48_750),
            "unicode" => ("\u0085\u2028", 39_000),
            "controls" => ("\n\0", 24_375),
            _ => throw new InvalidOperationException()
        };
        var text = "x" + string.Concat(Enumerable.Repeat(pattern, count));
        if (group)
        {
            var relay = new GroupRelay(source);
            relay.Install();
            var created = await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId,
                new()
                {
                    Name = "large message backup",
                    MemberCapacity = 10
                }, Token);
            await source.Client.GroupManager.SendMessageAsync(created.Ref,
                new()
                {
                    Body = new()
                    {
                        ContentType = "text/plain",
                        Text = text
                    }
                }, Token);
            var envelope = Assert.Single(relay.Events, entry => entry.Payload is GroupMessageEnvelope).Payload;
            Assert.Null(envelope.Validate(TestNetwork.Context));
            Assert.InRange(Encoding.UTF8.GetByteCount(envelope.ToJson()), 260_000, 262_144);
        }
        else
        {
            var sent = await source.Client.MessageManager.SendMessageAsync(source.Account.AccountId,
                new()
                {
                    Body = new()
                    {
                        ContentType = "text/plain",
                        Text = text
                    }
                }, Token);
            MessageSendRequest request;
            await using (var database = source.Database.Open())
                request = ProtocolModel.FromJson<MessageSendRequest>((await database.MessageOutbox.FindAsync([sent.MessageId], Token))!.RequestJson)!;
            Assert.Null(request.Envelope.Validate(TestNetwork.Context));
            Assert.InRange(Encoding.UTF8.GetByteCount(request.Envelope.ToJson()), 260_000, 262_144);
            source.Relay.Handler = (http, _) => Task.FromResult(http.Method == "message.timeline.sync" ? OfflineRelay.Json(new MessageTimelinePage
            {
                Items = long.Parse(RequestQuery.Parse(http)["after"]) < 5
                    ? [new() { Sequence = 5, AcceptedAt = request.Envelope.CreatedAt, Envelope = request.Envelope, KeyBox = request.RecipientBoxes[0] }] : [],
                Certificates = [source.Client.Device!],
                HasMore = false
            }) : source.Relay.Respond(http));
            await source.Client.MessageManager.SynchronizeAsync(source.Relay.RelayId, Token);
        }

        using var backup = new MemoryStream();
        await source.Client.ExportBackupAsync(backup, "large messages", new()
        {
            DirectHistory = BackupSelection.All,
            GroupHistory = BackupSelection.All
        }, Token);
        backup.Position = 0;
        var report = await target.Client.ImportBackupAsync(backup, "large messages", Token);
        Assert.Equal(0, report.Conflicts);
        await using var restored = target.Database.Open();
        var actual = group
            ? ProtocolModel.FromJson<GroupMessage>((await restored.GroupEvents.SingleAsync(value => value.IsMessage, Token)).DecryptedPayloadJson!)!.Body!.Text
            : ProtocolModel.FromJson<DirectMessage>((await restored.Messages.SingleAsync(Token)).PayloadJson!)!.Body!.Text;
        Assert.Equal(text, actual);
    }
}
