using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Validation;

namespace Meshline.Tests.Protocol;

public sealed class GroupAdmissionEntryTests
{
    [Theory]
    [InlineData(false, "valid")]
    [InlineData(false, "negative_time")]
    [InlineData(false, "payload")]
    [InlineData(false, "certificate")]
    [InlineData(true, "valid")]
    [InlineData(true, "negative_time")]
    [InlineData(true, "equal_times")]
    [InlineData(true, "earlier_expiry")]
    [InlineData(true, "payload")]
    [InlineData(true, "certificate")]
    public void Entries_validate_time_structure_and_nested_models(bool recovery, string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        using var account = new AccountSigner();
        var certificate = new DeviceSigner(account, Clock.Provider).Certificate;
        if (scenario == "certificate")
            certificate = certificate with
            {
                DeviceSignature = [.. new byte[64]]
            };
        var acceptedAt = scenario == "negative_time" ? -1 : 0;
        var expiresAt = scenario switch
        {
            "equal_times" => 0,
            "earlier_expiry" => -1,
            _ => 1
        };
        var memberKey = scenario == "payload" ? System.Collections.Immutable.ImmutableArray<byte>.Empty : certificate.EncryptionPublicKey;
        // Payload signature verification belongs to the component; entry validation checks its shape.
        ProtocolModel entry = recovery ? new GroupRecoveryEntry
        {
            Request = new()
            {
                GroupId = "grp_" + new string('A', 22),
                Account = account.AccountId,
                MemberEncryptionPublicKey = memberKey,
                DeviceSignature = [.. new byte[64]]
            },
            SignerCertificate = certificate,
            AcceptedAt = acceptedAt,
            ExpiresAt = expiresAt
        }

        : new GroupApplicationEntry
        {
            Application = new()
            {
                GroupId = "grp_" + new string('A', 22),
                InviteId = "inv_" + new string('A', 22),
                Account = account.AccountId,
                MemberEncryptionPublicKey = memberKey,
                DeviceSignature = [.. new byte[64]]
            },
            SignerCertificate = certificate,
            AcceptedAt = acceptedAt
        };

        Assert.Throws<ArgumentNullException>(() => entry.Validate(null));

        var violation = entry.Validate(TestNetwork.Context);
        if (scenario == "valid")
            Assert.Null(violation);
        else
        {
            var expected = scenario switch
            {
                "payload" => new ProtocolViolation(ProtocolViolationKind.Format, "The member encryption public key must contain 32 bytes."),
                "certificate" => new ProtocolViolation(ProtocolViolationKind.Signature, "The device signature is invalid."),
                _ => new ProtocolViolation(
                ProtocolViolationKind.Time,
                recovery ? "The recovery request has invalid identity or acceptance fields." : "The group application has invalid identity or acceptance fields.")
            };

            Assert.Equal(expected, violation);
        }
    }
}
