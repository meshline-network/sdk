using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Security.Cryptography;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupAdmissionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "time")]
    [InlineData(true, "time")]
    [InlineData(false, "payload")]
    [InlineData(true, "payload")]
    [InlineData(false, "certificate")]
    [InlineData(true, "certificate")]
    [InlineData(false, "group")]
    [InlineData(true, "group")]
    [InlineData(false, "account")]
    [InlineData(true, "account")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "signature")]
    [InlineData(true, "signature")]
    [InlineData(false, "cursor")]
    [InlineData(true, "cursor")]
    public async Task Admission_pages_preserve_entry_validation_matching_and_signatures(bool recovery, string scenario)
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
        using var otherAccount = new AccountSigner();
        var certificate = fixture.Client.Device!;
        var memberKey = scenario == "payload" ? System.Collections.Immutable.ImmutableArray<byte>.Empty : certificate.EncryptionPublicKey;
        var groupId = scenario == "group" ? "grp_" + new string('A', 22) : group.Ref.GroupId;
        TypedProtocolModel payload = recovery ? new GroupMemberRecoveryRequest
        {
            GroupId = groupId,
            Account = fixture.Account.AccountId,
            MemberEncryptionPublicKey = memberKey,
            DeviceSignature = []
        }

        : new GroupApplication
        {
            GroupId = groupId,
            InviteId = Identifiers.CreateInviteId(),
            Account = fixture.Account.AccountId,
            MemberEncryptionPublicKey = memberKey,
            DeviceSignature = []
        };
        var signature = scenario == "signature" ? new byte[64] : await fixture.Client.DeviceManager.SignAsync(payload.GetSigningInput(TestNetwork.Context, "device_signature"), Token);
        if (scenario == "account")
            certificate = new DeviceSigner(otherAccount, fixture.Relay.Clock).Certificate;
        if (scenario == "certificate")
            certificate = certificate with
            {
                DeviceSignature = [.. new byte[64]]
            };
        var acceptedAt = scenario == "time" ? -1 : 0;
        ProtocolModel page;
        ProtocolModel entry;
        if (recovery)
        {
            var value = new GroupRecoveryEntry
            {
                Request = ((GroupMemberRecoveryRequest)payload) with
                {
                    DeviceSignature = [.. signature]
                },
                SignerCertificate = certificate,
                AcceptedAt = acceptedAt,
                ExpiresAt = 1
            };
            entry = value;
            page = new GroupRecoveryPage
            {
                Requests = scenario == "duplicate" ? [value, value] : [value],
                Next = scenario == "cursor" ? "repeat" : null
            };
        }
        else
        {
            var value = new GroupApplicationEntry
            {
                Application = ((GroupApplication)payload) with
                {
                    DeviceSignature = [.. signature]
                },
                SignerCertificate = certificate,
                AcceptedAt = acceptedAt
            };
            entry = value;
            page = new GroupApplicationPage
            {
                Applications = scenario == "duplicate" ? [value, value] : [value],
                Next = scenario == "cursor" ? "repeat" : null
            };
        }

        if (scenario is "group" or "account" or "signature" or "duplicate" or "cursor")
            Assert.Null(entry.Validate(TestNetwork.Context));
        var method = recovery ? "group.member.recovery.list" : "group.application.list";
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == method ? OfflineRelay.Json(page) : relay.Respond(request));
        var requestPage = scenario == "cursor" ? new PageRequest
        {
            Cursor = "repeat"
        }

        : null;
        async Task ReadAsync()
        {
            if (recovery)
            {
                var actual = Assert.Single((await manager.GetKeyRecoveryRequestsAsync(group.Ref, requestPage, Token)).Items);

                Assert.Equal(fixture.Account.AccountId, actual.Request.Account);
                Assert.Equal(DateTimeOffset.UnixEpoch, actual.AcceptedAt);
                Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), actual.ExpiresAt);
            }
            else
            {
                var actual = Assert.Single((await manager.GetApplicationsAsync(group.Ref, requestPage, Token)).Items);

                Assert.Equal(fixture.Account.AccountId, actual.Application.Account);
                Assert.Equal(DateTimeOffset.UnixEpoch, actual.AcceptedAt);
            }
        }

        if (scenario == "valid")
            await ReadAsync();
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
                "payload" => "The member encryption public key must contain 32 bytes.",
                "certificate" => "The device signature is invalid.",
                "cursor" => recovery ? "The recovery page is invalid." : "The application page is invalid.",
                _ => recovery ? "The recovery request has invalid identity or acceptance fields." : "The group application has invalid identity or acceptance fields."
            }, error.Message);
        }
    }
}
