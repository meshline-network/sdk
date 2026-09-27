using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Security.Cryptography;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupInviteTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "uses")]
    [InlineData(true, "uses")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "group")]
    [InlineData(true, "group")]
    [InlineData(false, "account")]
    [InlineData(true, "account")]
    [InlineData(false, "signature")]
    [InlineData(true, "signature")]
    [InlineData(false, "invite_id")]
    public async Task Resolve_and_list_preserve_invitation_validation_and_matching(bool list, string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, new()
        {
            Name = "offline",
            MemberCapacity = 10,
            InvitePolicy = GroupInvitePolicy.Administrators
        }, Token);
        var created = await manager.CreateInviteAsync(group.Ref, fixture.Relay.Clock.GetUtcNow().AddHours(1), cancellationToken: Token);
        using var otherAccount = new AccountSigner();
        var invite = created.Document;
        var certificate = fixture.Client.Device!;
        switch (scenario)
        {
            case "expired":
                invite = invite with
                {
                    CreatedAt = invite.CreatedAt - 60,
                    ExpiresAt = invite.CreatedAt
                };
                break;
            case "group":
                invite = invite with
                {
                    GroupId = "grp_" + new string('A', 22)
                };
                break;
            case "account":
                certificate = new DeviceSigner(otherAccount, fixture.Relay.Clock).Certificate;
                break;
            case "signature":
                invite = invite with
                {
                    DeviceSignature = [.. new byte[64]]
                };
                break;
            case "invite_id":
                invite = invite with
                {
                    InviteId = Identifiers.CreateInviteId()
                };
                break;
        }

        var result = new GroupInviteResolveResult
        {
            Invite = invite,
            SignerCertificate = certificate,
            Uses = scenario == "uses" ? -1 : 0
        };
        if (scenario is "group" or "account" or "signature" or "invite_id")
            Assert.Null(result.Validate(TestNetwork.Context));
        var page = new GroupInvitePage
        {
            Invites = [new()
            {
                Invite = invite,
                SignerDeviceId = certificate.GetDeviceId(TestNetwork.Context),
                Uses = result.Uses
}
            ],
            Certificates = [certificate]
        };
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method switch
        {
            "group.invite.resolve" => OfflineRelay.Json(result),
            "group.invite.list" => OfflineRelay.Json(page),
            _ => relay.Respond(request)
        });
        async Task<GroupInviteInfo> ReadAsync() => list ? Assert.Single((await manager.GetInvitesAsync(group.Ref, cancellationToken: Token)).Items) : await manager.GetInviteAsync(new(group.Ref, created.Document.InviteId), Token);
        if (scenario == "valid")
        {
            var actual = await ReadAsync();

            Assert.Equal(created.Document.ToJson(), actual.Invite.Document.ToJson());
            Assert.Equal(0, actual.Uses);
        }
        else if (scenario == "signature")
        {
            var error = await Assert.ThrowsAsync<CryptographicException>(ReadAsync);

            Assert.Equal("The group object has an invalid device signature.", error.Message);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(ReadAsync);

            Assert.Equal(scenario switch
            {
                "expired" => "The group invitation has expired.",
                "invite_id" => "The relay returned another invitation.",
                _ => "The group invitation has invalid identity or usage fields."
            }, error.Message);
        }
    }
}
