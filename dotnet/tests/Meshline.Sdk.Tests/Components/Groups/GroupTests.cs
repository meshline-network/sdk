using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static GroupCreateOptions Options => new()
    {
        Name = "offline",
        MemberCapacity = 10,
        InvitePolicy = GroupInvitePolicy.Administrators
    };

    [Theory]
    [InlineData("chat", true)]
    [InlineData("nickname", false)]
    public async Task Message_notification_intent_follows_content_type(string content, bool notify)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var relay = new GroupRelay(fixture);
        relay.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);

        if (content == "chat")
            await manager.SendMessageAsync(group.Ref, new() { Body = new() { ContentType = "text/plain", Text = "hello" } }, Token);
        else
            await manager.SetNicknameAsync(group.Ref, "member", Token);

        var envelope = Assert.IsType<GroupMessageEnvelope>(relay.Events.Last().Payload);
        Assert.Equal(notify, envelope.Notify);
        Assert.True(Ed25519.Verify(envelope.DeviceSignature.AsSpan(), fixture.Client.Device!.SigningPublicKey.AsSpan(), envelope.GetSigningInput(TestNetwork.Context)));
        await using var database = fixture.Database.Open();
        var stored = await database.GroupEvents.SingleAsync(row => row.MessageId == envelope.MessageId, Token);
        var plaintext = ProtocolModel.FromJson<TypedProtocolModel>(stored.DecryptedPayloadJson!)!;
        Assert.DoesNotContain("notify", plaintext.AdditionalProperties.Keys);
    }

    [Fact]
    public async Task Group_key_synchronization_does_not_request_push()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var relay = new GroupRelay(fixture);
        relay.Install();

        await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);

        await using var database = fixture.Database.Open();
        var synchronization = await database.MessageOutbox.ToListAsync(Token);
        Assert.NotEmpty(synchronization);
        Assert.All(synchronization, row =>
            Assert.False(ProtocolModel.FromJson<MessageSendRequest>(row.RequestJson)!.Envelope.Notify));
    }

    [Fact]
    public async Task Uncertain_message_retries_the_exact_envelope_across_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var relay = new GroupRelay(fixture);
        relay.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        var requests = new List<string>();
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "group.message.send")
            {
                requests.Add(request.Body!);
                if (requests.Count == 1) throw new HttpRequestException("Connection lost before the send result was known.");
            }
            return Task.FromResult(relay.Respond(request));
        };
        var draft = new GroupMessageDraft { Body = new() { ContentType = "text/plain", Text = "recover me" } };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.GroupManager.SendMessageAsync(group.Ref, draft, Token));
        await using (var database = fixture.Database.Open())
        {
            var operation = await database.GroupOperations.SingleAsync(Token);
            Assert.Equal(Assert.Single(requests), operation.RequestJson);
            var envelope = ProtocolModel.FromJson<GroupMessageEnvelope>(operation.RequestJson)!;
            Assert.True(envelope.Notify);
            Assert.True(Ed25519.Verify(envelope.DeviceSignature.AsSpan(), fixture.Client.Device!.SigningPublicKey.AsSpan(), envelope.GetSigningInput(TestNetwork.Context)));
        }
        await fixture.ReopenAsync();
        var observed = AsyncTest.Signal();
        Exception? rejection = null;
        var manager = fixture.Client.GroupManager;
        manager.TimelineChanged += (_, _) => observed.TrySetResult();
        manager.BackgroundError += (_, error) => { rejection = error.Error; observed.TrySetResult(); };
        await manager.StartAsync(Token);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await manager.StopAsync(Token);
        Assert.Null(rejection);
        Assert.Equal(2, requests.Count);
        Assert.Single(requests.Distinct());
        await using (var database = fixture.Database.Open())
        {
            Assert.Empty(await database.GroupOperations.ToListAsync(Token));
            var stored = await database.GroupEvents.SingleAsync(row => row.IsMessage, Token);
            Assert.Equal(requests[0], stored.PayloadJson);
            var plaintext = ProtocolModel.FromJson<GroupMessage>(stored.DecryptedPayloadJson!)!;
            Assert.Equal("recover me", plaintext.Body!.Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invitation_preview_requires_the_requested_group_id(bool mismatched)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        var invite = await manager.CreateInviteAsync(group.Ref, fixture.Relay.Clock.GetUtcNow().AddHours(1), cancellationToken: Token);
        var preview = group.Group with
        {
            GroupId = mismatched ? "grp_" + new string('A', 43) : group.Ref.GroupId,
            Name = "preview"
        };
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "group.resolve" ? OfflineRelay.Json(preview) : relay.Respond(request));
        if (mismatched)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.GetGroupAsync(invite, Token));

            Assert.Equal("The relay returned an invalid group preview.", error.Message);

            await using var database = fixture.Database.Open();

            Assert.Equal("offline", (await database.Groups.SingleAsync(Token)).Name);
        }
        else
        {
            var resolved = await manager.GetGroupAsync(invite, Token);

            Assert.Equal(group.Ref.GroupId, resolved.Group.GroupId);
            Assert.Equal(group.Group.Name, resolved.Group.Name);
        }
    }

    [Fact]
    public async Task Create_encrypt_rotate_recover_and_close_survive_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);

        Assert.Equal(GroupRole.Owner, group.Role);

        await manager.UpdateGroupAsync(group.Ref, new() { Name = new("updated") }, Token);
        var original = await manager.SendMessageAsync(group.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "before rotation"
            }
        }, Token);

        Assert.Equal("before rotation", original.Body!.Text);

        await manager.RotateSecretAsync(group.Ref, true, Token);
        var recovery = await manager.RequestKeyRecoveryAsync(group.Ref, Token);

        Assert.Equal(fixture.Account.AccountId, recovery.Request.Account);

        await manager.ApproveKeyRecoveryAsync(group.Ref, [fixture.Account.AccountId], Token);
        await manager.SendMessageAsync(group.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "after recovery"
            }
        }, Token);

        await fixture.ReopenAsync();
        await using (var messages = await fixture.Client.GroupManager.GetMessagesAsync(group.Ref.GroupId, cancellationToken: Token))
            Assert.Equal(["before rotation", "after recovery"], (await messages.ReadNextAsync(10, Token)).Select(value => value.Body!.Text));
        await using (var db = fixture.Database.Open())
        {
            var epochs = await db.GroupEpochs.OrderBy(value => value.Epoch).ToListAsync(Token);

            Assert.Equal(3, epochs.Count);
            Assert.All(epochs, value =>
            {
                Assert.NotNull(value.ProtectedApplicationSecret);
                Assert.NotNull(value.ProtectedClientSecret);
            });
            Assert.NotEqual(epochs[0].Commitment, epochs[1].Commitment);
            Assert.Equal(epochs[1].Commitment, epochs[2].Commitment);
            Assert.Empty(await db.GroupOperations.ToListAsync(Token));
            Assert.Empty(await db.GroupRotations.ToListAsync(Token));
        }

        await fixture.Client.GroupManager.CloseGroupAsync(group.Ref, Token);

        await fixture.ReopenAsync();

        Assert.Equal(GroupStatus.Closed, (await fixture.Client.GroupManager.GetGroupAsync(group.Ref, Token)).Group.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.GroupManager.SendMessageAsync(group.Ref, new(), Token));
    }

    [Fact]
    public async Task Admission_roles_removal_and_bans_are_verified_and_persisted()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        using var peer = new AccountSigner();
        var device = fixture.Relay.AddPeer(peer);
        var relay = new GroupRelay(fixture);
        relay.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        var invite = await manager.CreateInviteAsync(group.Ref, peer.AccountId, fixture.Relay.Clock.GetUtcNow().AddHours(1), Token);
        await relay.AddApplicationAsync(device, invite.Document, Token);
        await manager.ApproveApplicationsAsync(group.Ref, [peer.AccountId], Token);
        await manager.SetRoleAsync(group.Ref, peer.AccountId, GroupRole.Administrator, Token);
        await using (var members = await manager.GetMembersAsync(group.Ref, cancellationToken: Token))
            Assert.Equal(GroupRole.Administrator, (await members.ReadNextAsync(10, Token)).Single(value => value.AccountId == peer.AccountId).Role);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.RemoveMembersAsync(group.Ref, [fixture.Account.AccountId], Token));

        await manager.RemoveMembersAsync(group.Ref, [peer.AccountId], Token);
        await manager.BanAsync(group.Ref, [peer.AccountId], Token);
        await using (var bans = await manager.GetBansAsync(group.Ref, cancellationToken: Token))
            Assert.Equal(peer.AccountId, Assert.Single(await bans.ReadNextAsync(10, Token)));
        await manager.UnbanAsync(group.Ref, [peer.AccountId], Token);

        await fixture.ReopenAsync();
        await using var restored = await fixture.Client.GroupManager.GetMembersAsync(group.Ref, cancellationToken: Token);

        Assert.Equal(fixture.Account.AccountId, Assert.Single(await restored.ReadNextAsync(10, Token)).AccountId);
    }

    [Fact]
    public async Task Accepted_rotation_with_lost_response_recovers_without_replaying_write()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        relay.LoseResponseFor = "group.secret.rotation.commit";

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.GroupManager.RotateSecretAsync(group.Ref, true, Token));

        await fixture.ReopenAsync();
        var ready = AsyncTest.Signal();
        fixture.Client.GroupManager.GroupChanged += (_, _) => ready.TrySetResult();
        fixture.Client.GroupManager.BackgroundError += (_, error) => ready.TrySetException(error.Error);
        await fixture.Client.GroupManager.StartAsync(Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.GroupManager.StopAsync(Token);
        await fixture.Client.GroupManager.GetGroupAsync(group.Ref, Token);
        await using var verify = fixture.Database.Open();

        Assert.Empty(await verify.GroupOperations.ToListAsync(Token));
        Assert.Empty(await verify.GroupRotations.ToListAsync(Token));
        Assert.Single(fixture.Relay.Requests, request => request.Method == "group.secret.rotation.commit");
        Assert.NotNull((await verify.GroupEpochs.SingleAsync(value => value.Epoch == 1, Token)).ProtectedApplicationSecret);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("chain")]
    [InlineData("epoch")]
    public async Task Invalid_management_page_rolls_back_every_event_and_cursor(string defect)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        // Feed two signed updates as a single page; the second is invalid. No first update may leak into storage.
        var previous = relay.Events[0].Payload;
        var hash = "sha256:" + System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(previous.GetSigningInput(TestNetwork.Context)));
        var first = new Models.Protocol.GroupUpdate
        {
            GroupId = group.Ref.GroupId,
            PrevHash = hash,
            Name = new("uncommitted"),
            DeviceSignature = []
        };
        first = first with
        {
            DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(first.GetSigningInput(TestNetwork.Context), Token)]
        };
        var second = first with
        {
            PrevHash = defect == "chain" ? hash : "sha256:" + System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(first.GetSigningInput(TestNetwork.Context))),
            Name = new("invalid")
        };
        second = second with
        {
            DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(second.GetSigningInput(TestNetwork.Context), Token)]
        };
        if (defect == "signature")
            second = second with
            {
                DeviceSignature = [.. new byte[64]]
            };
        relay.Events.Add(relay.Events[0] with
        {
            Sequence = 1,
            Payload = first
        });
        relay.Events.Add(relay.Events[0] with
        {
            Sequence = 2,
            Epoch = defect == "epoch" ? 1 : 0,
            Payload = second
        });
        var failure = await Record.ExceptionAsync(() => fixture.Client.GroupManager.GetGroupAsync(group.Ref, Token));

        Assert.True(failure is InvalidDataException or CryptographicException, failure?.ToString());

        await using var verify = fixture.Database.Open();
        var stored = await verify.Groups.SingleAsync(Token);

        Assert.Equal(0, stored.Sequence);
        Assert.Equal("offline", stored.Name);
        Assert.Single(await verify.GroupEvents.ToListAsync(Token));
    }

    [Fact]
    public async Task Rejected_write_leaves_no_pending_work()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);
        relay.RejectNextWrite = true;

        await Assert.ThrowsAsync<RelayException>(() => fixture.Client.GroupManager.UpdateGroupAsync(group.Ref, new() { Name = new("rejected") }, Token));

        await using var verify = fixture.Database.Open();

        Assert.Empty(await verify.GroupOperations.ToListAsync(Token));
        Assert.Equal("offline", (await verify.Groups.SingleAsync(Token)).Name);
    }

    [Fact]
    public async Task Canceled_rotation_leaves_no_pending_work()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var group = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, Options, Token);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.GroupManager.RotateSecretAsync(group.Ref, cancellationToken: canceled.Token));

        await using var verify = fixture.Database.Open();

        Assert.Empty(await verify.GroupOperations.ToListAsync(Token));
        Assert.Equal("offline", (await verify.Groups.SingleAsync(Token)).Name);
    }
}
