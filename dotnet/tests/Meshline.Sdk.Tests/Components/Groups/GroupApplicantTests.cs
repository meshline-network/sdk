using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Net;
using ClientInvite = Meshline.Models.Client.GroupInvite;
using SignedInvite = Meshline.Models.Protocol.GroupInvite;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupApplicantTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nonmember_can_preview_and_apply_without_reading_member_invitation_records(bool targeted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var owner = new AccountSigner();
        var invite = await InviteAsync(fixture, owner, targeted ? fixture.Account.AccountId : null);
        var preview = Preview(invite, owner);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method switch
        {
            "group.invite.resolve" => OfflineRelay.Error("forbidden"),
            "group.resolve" => OfflineRelay.Json(preview),
            "group.application.submit" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => fixture.Relay.Respond(request)
        });

        var resolved = await fixture.Client.GroupManager.GetGroupAsync(invite, Token);
        Assert.Equal(preview, resolved.Group);
        Assert.NotEqual(GroupMembershipState.Member, resolved.Membership);
        await fixture.Client.GroupManager.ApplyToGroupAsync(invite, Token);

        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "group.invite.resolve");
        var previews = fixture.Relay.Requests.Where(request => request.Method == "group.resolve").ToList();
        Assert.Equal(2, previews.Count);
        Assert.All(previews, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Verb);
            var query = RequestQuery.Parse(request);
            Assert.Equal(invite.Group.GroupId, query["group_id"]);
            Assert.Equal(invite.Document.InviteId, query["invite_id"]);
        });
        var submitted = Assert.Single(fixture.Relay.Requests, request => request.Method == "group.application.submit");
        Assert.Equal(HttpMethod.Post, submitted.Verb);
        var application = ProtocolModel.FromJson<GroupApplication>(submitted.Body!)!;
        Assert.Equal(invite.Group.GroupId, application.GroupId);
        Assert.Equal(invite.Document.InviteId, application.InviteId);
        Assert.Equal(fixture.Account.AccountId, application.Account);
        Assert.Null(application.Validate(TestNetwork.Context));
        Assert.True(Ed25519.Verify(application.DeviceSignature.AsSpan(), fixture.Client.Device!.SigningPublicKey.AsSpan(), application.GetSigningInput(TestNetwork.Context, "device_signature")));
        await using var database = fixture.Database.Open();
        Assert.Equal(GroupMembershipState.Pending, (await database.Groups.SingleAsync(Token)).Membership);
        Assert.NotEmpty((await database.GroupMemberKeys.SingleAsync(Token)).ProtectedPrivateKey);
        Assert.Empty(await database.GroupOperations.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false, "target")]
    [InlineData(true, "target")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "forbidden")]
    [InlineData(true, "forbidden")]
    [InlineData(false, "group")]
    [InlineData(true, "group")]
    public async Task Rejected_invitation_or_preview_cannot_create_an_application(bool apply, string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var owner = new AccountSigner();
        var invite = await InviteAsync(fixture, owner, scenario == "target" ? owner.AccountId : null);
        if (scenario == "expired") invite = new(invite.Group.RelayId, invite.Document with { ExpiresAt = Clock.UtcNow.ToUnixTimeSeconds() });
        var preview = Preview(invite, owner);
        if (scenario == "group") preview = preview with { GroupId = "grp_" + new string('B', 22) };
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method switch
        {
            "group.invite.resolve" => OfflineRelay.Error("forbidden"),
            "group.resolve" => scenario == "forbidden" ? OfflineRelay.Error("forbidden") : OfflineRelay.Json(preview),
            _ => fixture.Relay.Respond(request)
        });
        async Task ExecuteAsync()
        {
            if (apply) await fixture.Client.GroupManager.ApplyToGroupAsync(invite, Token);
            else await fixture.Client.GroupManager.GetGroupAsync(invite, Token);
        }

        if (scenario == "forbidden") await Assert.ThrowsAsync<RelayException>(ExecuteAsync);
        else if (scenario == "target" && !apply) await Assert.ThrowsAsync<UnauthorizedAccessException>(ExecuteAsync);
        else await Assert.ThrowsAsync<InvalidDataException>(ExecuteAsync);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "group.invite.resolve" or "group.application.submit");
        if (scenario is "target" or "expired")
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "group.resolve");
        await using var database = fixture.Database.Open();
        Assert.Empty(await database.Groups.ToListAsync(Token));
        Assert.Empty(await database.GroupMemberKeys.ToListAsync(Token));
        Assert.Empty(await database.GroupOperations.ToListAsync(Token));
    }

    static async Task<ClientInvite> InviteAsync(TestClient fixture, AccountSigner owner, string? invitee)
    {
        var signer = new DeviceSigner(owner, fixture.Relay.Clock);
        var invite = new SignedInvite
        {
            GroupId = "grp_" + new string('A', 22),
            InviteId = Identifiers.CreateInviteId(),
            Inviter = owner.AccountId,
            Invitee = invitee,
            CreatedAt = Clock.UtcNow.ToUnixTimeSeconds() - 60,
            ExpiresAt = Clock.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            DeviceSignature = []
        };
        return new(fixture.Relay.RelayId, invite with
        {
            DeviceSignature = [.. await signer.SignAsync(invite.GetSigningInput(TestNetwork.Context, "device_signature"), Token)]
        });
    }

    static GroupState Preview(ClientInvite invite, AccountSigner owner) => new()
    {
        GroupId = invite.Group.GroupId,
        Name = "invitation preview",
        Status = GroupStatus.Active,
        Owner = owner.AccountId,
        MemberCapacity = 10,
        MemberCount = 1,
        InvitePolicy = GroupInvitePolicy.Administrators
    };
}
