using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Backups;

public sealed class BackupRoundtripTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "backup test password \U0001F510";
    static readonly BackupOptions WithHistory = new() { DirectHistory = BackupSelection.All, GroupHistory = BackupSelection.All };
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Fact]
    public async Task File_roundtrip_without_group_secrets_does_not_require_a_local_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync(establish: false);
        await target.InitializeAsync(establish: false);
        using var peer = new AccountSigner();
        source.Relay.AddPeer(peer);
        var state = source.Relay.Devices[peer.AccountId];
        const string groupId = "grp_AAAAAAAAAAAAAAAAAAAAAA";
        const string channelId = "chan_AAAAAAAAAAAAAAAAAAAAAA";
        await using (var db = source.Database.Open())
        {
            db.DeviceStates.Add(new()
            {
                AccountId = peer.AccountId,
                Revision = state.Revision,
                DocumentJson = state.ToJson()
            });
            db.Contacts.Add(new()
            {
                AccountId = peer.AccountId,
                State = ContactRelationshipState.Active,
                UpdatedAt = Clock.UtcNow
            });
            db.Messages.Add(Message(source, peer.AccountId, "restored without a device"));
            db.Groups.Add(new()
            {
                GroupId = groupId,
                RelayId = source.Relay.RelayId
            });
            db.Channels.Add(new()
            {
                ChannelId = channelId,
                RelayId = source.Relay.RelayId,
                IsFollowed = true
            });
            await db.SaveChangesAsync(Token);
        }
        var sourceRequests = source.Relay.Requests.Count;
        var targetRequests = target.Relay.Requests.Count;
        using var output = new MemoryStream();
        Assert.Equal(5, (await source.Client.ExportBackupAsync(output, Password, WithHistory, Token)).Applied);
        output.Position = 0;
        Assert.Equal(5, (await target.Client.ImportBackupAsync(output, Password, Token)).Applied);
        await using (var db = target.Database.Open())
        {
            Assert.Equal(state.ToJson(), (await db.DeviceStates.SingleAsync(Token)).DocumentJson);
            Assert.Equal(peer.AccountId, (await db.Contacts.SingleAsync(Token)).AccountId);
            Assert.Equal(peer.AccountId, (await db.Messages.SingleAsync(Token)).Sender);
            Assert.Equal(groupId, (await db.Groups.SingleAsync(Token)).GroupId);
            Assert.Equal(channelId, (await db.Channels.SingleAsync(Token)).ChannelId);
            Assert.Empty(await db.LocalDevices.ToListAsync(Token));
            Assert.Null((await db.Bindings.SingleAsync(Token)).DeviceId);
        }
        using var restored = new MemoryStream();
        Assert.Equal(5, (await target.Client.ExportBackupAsync(restored, Password, WithHistory, Token)).Applied);
        restored.Position = 0;
        var duplicate = await source.Client.ImportBackupAsync(restored, Password, Token);
        Assert.Equal(0, duplicate.Applied);
        Assert.Equal(5, duplicate.Unchanged);
        Assert.Null(source.Client.Device);
        Assert.Null(target.Client.Device);
        Assert.Equal(sourceRequests, source.Relay.Requests.Count);
        Assert.Equal(targetRequests, target.Relay.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Roundtrip_merges_contacts_history_and_all_group_secrets_under_new_device_protection(bool history)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        using var deleted = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(source, peer, Token);
        var groupRelay = new GroupRelay(source);
        groupRelay.Install();
        var group = await source.Client.GroupManager.CreateGroupAsync(source.Relay.RelayId, new()
        {
            Name = "backed up",
            MemberCapacity = 10
        }, Token);
        await source.Client.GroupManager.SendMessageAsync(group.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "group history"
            }
        }, Token);
        var sourceDevice = source.Client.Device!.GetDeviceId(TestNetwork.Context);
        var targetDevice = target.Client.Device!.GetDeviceId(TestNetwork.Context);
        Assert.NotEqual(sourceDevice, targetDevice);
        var channel = "chan_AAAAAAAAAAAAAAAAAAAAAA";
        StoredMessageRecord existing;
        await using (var db = source.Database.Open())
        {
            // Keep an expired grant as historical consent, without extending it on import.
            var contact = await db.Contacts.SingleAsync(row => row.AccountId == peer.AccountId, Token);
            var grant = ProtocolModel.FromJson<ContactGrant>(contact.GrantToJson!)!;
            contact.GrantToJson = (grant with
            {
                ExpiresAt = Clock.UtcNow.AddDays(-1).ToUnixTimeSeconds()
            }).ToJson();
            db.Contacts.Add(new()
            {
                AccountId = deleted.AccountId,
                State = ContactRelationshipState.Deleted,
                UpdatedAt = Clock.UtcNow
            });
            existing = Message(source, peer.AccountId, "existing");
            db.Messages.AddRange(existing, Message(source, peer.AccountId, "imported"));
            db.Channels.Add(new()
            {
                ChannelId = channel,
                RelayId = source.Relay.RelayId,
                IsFollowed = true
            });
            await db.SaveChangesAsync(Token);
        }
        await using (var db = target.Database.Open())
        {
            db.Contacts.Add(new()
            {
                AccountId = deleted.AccountId,
                State = ContactRelationshipState.Active,
                UpdatedAt = Clock.UtcNow.AddDays(-1)
            });
            db.Channels.Add(new()
            {
                ChannelId = channel,
                RelayId = source.Relay.RelayId,
                IsFollowed = false
            });
            existing.LocalSequence = 0;
            db.Messages.Add(existing);
            await db.SaveChangesAsync(Token);
        }
        await source.Client.StopAsync(Token);
        await target.Client.StopAsync(Token);
        var client = target.Client;
        var messages = client.MessageManager;
        using var backup = new MemoryStream();
        await source.Client.ExportBackupAsync(backup, Password, new()
        {
            DirectHistory = history ? BackupSelection.All : BackupSelection.None,
            GroupHistory = history ? BackupSelection.All : BackupSelection.None
        }, Token);
        backup.Position = 0;
        var report = await client.ImportBackupAsync(backup, Password, Token);
        Assert.Equal(0, report.Conflicts);
        backup.Position = 0;
        var repeated = await client.ImportBackupAsync(backup, Password, Token);
        Assert.Equal(0, repeated.Applied);
        Assert.Equal(0, repeated.Conflicts);
        await using (var db = target.Database.Open())
        {
            Assert.Equal(targetDevice, (await db.Bindings.SingleAsync(Token)).DeviceId);
            Assert.Equal(ContactRelationshipState.Deleted, (await db.Contacts.FindAsync([deleted.AccountId], Token))!.State);
            var restored = ProtocolModel.FromJson<ContactGrant>((await db.Contacts.FindAsync([peer.AccountId], Token))!.GrantToJson!)!;
            Assert.True(restored.ExpiresAt < Clock.UtcNow.ToUnixTimeSeconds());
            Assert.Equal(source.Relay.Devices[peer.AccountId].ToJson(), (await db.DeviceStates.FindAsync([peer.AccountId], Token))!.DocumentJson);
            Assert.False((await db.Channels.SingleAsync(Token)).IsFollowed);
            Assert.Equal(history ? 2 : 1, await db.Messages.CountAsync(Token));
            Assert.Equal(history ? 1 : 0, await db.GroupEvents.CountAsync(row => row.IsMessage, Token));
            Assert.Empty(await db.MessageOutbox.ToListAsync(Token));
            Assert.Empty(await db.ConversationReads.ToListAsync(Token));
            Assert.Empty(await db.GroupOperations.ToListAsync(Token));
            Assert.Empty(await db.GroupRotations.ToListAsync(Token));
            await using var original = source.Database.Open();
            foreach (var row in await db.GroupMemberKeys.ToListAsync(Token))
            {
                var old = (await original.GroupMemberKeys.FindAsync([row.GroupId, row.PublicKey], Token))!;
                await SameSecret(old.ProtectedPrivateKey, row.ProtectedPrivateKey, row.GroupId, "member", row.PublicKey);
                Assert.False(row.Shared);
            }
            foreach (var row in await db.GroupEpochs.ToListAsync(Token))
            {
                var old = (await original.GroupEpochs.FindAsync([row.GroupId, row.Epoch], Token))!;
                Assert.Null(row.KeyEntryJson);
                await SameSecret(old.ProtectedClientSecret!, row.ProtectedClientSecret!, row.GroupId, "client", row.Commitment!);
                await SameSecret(old.ProtectedApplicationSecret!, row.ProtectedApplicationSecret!, row.GroupId, "epoch", row.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        Assert.Same(client, target.Client);
        Assert.Same(messages, target.Client.MessageManager);
        Assert.Equal(ComponentState.Stopped, client.LifecycleState);
        Assert.NotNull(await messages.GetContactAsync(peer.AccountId, Token));
        Assert.Equal(history ? 2 : 1, (await target.Client.GetConversationAsync(peer.AccountId, Token))!.UnreadCount);

        async Task SameSecret(byte[] before, byte[] after, string id, string kind, string version)
        {
            string Purpose(string device) => $"Meshline/{TestNetwork.Context}/{source.Account.AccountId}/{device}/group/{id}/{kind}/{version}";
            Assert.NotEqual(before, after);
            Assert.Equal(await source.Protector.UnprotectAsync(before, Purpose(sourceDevice), Token), await target.Protector.UnprotectAsync(after, Purpose(targetDevice), Token));
        }
    }

    [Fact]
    public async Task Merge_preserves_newer_deletions_equal_version_consent_and_conflicting_message_contents()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        using var other = new AccountSigner();
        var message = Message(source, peer.AccountId, "source");
        await using (var db = source.Database.Open())
        {
            db.Contacts.AddRange(new()
            {
                AccountId = peer.AccountId,
                State = ContactRelationshipState.Active,
                UpdatedAt = Clock.UtcNow.AddDays(-1)
            },
                new()
                {
                    AccountId = other.AccountId,
                    State = ContactRelationshipState.Active,
                    Alias = "source",
                    UpdatedAt = Clock.UtcNow
                });
            db.Messages.Add(message);
            await db.SaveChangesAsync(Token);
        }
        message.LocalSequence = 0;
        message.PayloadJson = new DirectMessage { Body = new() { ContentType = "text/plain", Text = "target" } }.ToJson();
        await using (var db = target.Database.Open())
        {
            db.Contacts.AddRange(new()
            {
                AccountId = peer.AccountId,
                State = ContactRelationshipState.Deleted,
                UpdatedAt = Clock.UtcNow
            },
                new()
                {
                    AccountId = other.AccountId,
                    State = ContactRelationshipState.Active,
                    Alias = "target",
                    UpdatedAt = Clock.UtcNow
                });
            db.Messages.Add(message);
            await db.SaveChangesAsync(Token);
        }
        await source.Client.StopAsync(Token);
        await target.Client.StopAsync(Token);
        using var output = new MemoryStream();
        await source.Client.ExportBackupAsync(output, Password, WithHistory, cancellationToken: Token);
        output.Position = 0;
        var report = await target.Client.ImportBackupAsync(output, Password, Token);
        Assert.Equal(2, report.Conflicts);
        await using var check = target.Database.Open();
        Assert.Equal(ContactRelationshipState.Deleted, (await check.Contacts.FindAsync([peer.AccountId], Token))!.State);
        Assert.Equal("target", (await check.Contacts.FindAsync([other.AccountId], Token))!.Alias);
        Assert.Contains("target", (await check.Messages.SingleAsync(Token)).PayloadJson!);
    }

    static StoredMessageRecord Message(TestClient fixture, string sender, string text) => new()
    {
        MessageId = Identifiers.CreateMessageId(),
        Sender = sender,
        Recipient = fixture.Account.AccountId,
        SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(Clock.UtcNow.ToUnixTimeSeconds()),
        IsDirect = true,
        PayloadType = "meshline.message.direct",
        PayloadJson = new DirectMessage { Body = new() { ContentType = "text/plain", Text = text } }.ToJson()
    };
}
