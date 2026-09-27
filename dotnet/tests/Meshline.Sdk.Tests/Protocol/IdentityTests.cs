using Meshline.Identity;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Validation;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Protocol;

public sealed class IdentityTests
{
    static DeviceCertificate Certificate()
    {
        var vector = ProtocolVectors.Read("identity-auth").GetProperty("device_certificate");
        var node = JsonNode.Parse(vector.GetProperty("unsigned_object").GetRawText())!;
        node["device_signature"] = vector.GetProperty("device_signature").GetString();
        node["account_signature"] = vector.GetProperty("account_signature").GetString();
        return ProtocolModel.FromJson<DeviceCertificate>(node.ToJsonString())!;
    }

    [Fact]
    public void Certificate_matches_both_signing_inputs_and_device_identity()
    {
        var vector = ProtocolVectors.Read("identity-auth");
        var signed = vector.GetProperty("device_certificate");
        var certificate = Certificate();

        Assert.Equal(Convert.FromHexString(signed.GetProperty("device_signing_input_utf8_hex").GetString()!), certificate.GetDeviceSigningInput(TestNetwork.Context));
        Assert.Equal(Convert.FromHexString(signed.GetProperty("account_signing_input_utf8_hex").GetString()!), certificate.GetAccountSigningInput(TestNetwork.Context));

        var expectedDeviceId = vector.GetProperty("device_identity").GetProperty("derived_device_id").GetString();

        Assert.Equal(expectedDeviceId, Identifiers.DeriveDeviceId(certificate, TestNetwork.Context));
        Assert.Equal(expectedDeviceId, certificate.GetDeviceId(TestNetwork.Context));
        Assert.Null(certificate.Validate(TestNetwork.Context));
        Assert.Equal(certificate.Account, AccountAdapter.GetAccountId("neo:860833102", certificate.AccountPublicKey.AsSpan()));
    }

    [Theory]
    [InlineData("device")]
    [InlineData("account")]
    [InlineData("context")]
    [InlineData("key")]
    public void Certificate_rejects_tampering_and_cross_network_use(string target)
    {
        var certificate = Certificate();
        var context = TestNetwork.Context;
        if (target == "device")
            certificate = certificate with
            {
                DeviceSignature = Mutate(certificate.DeviceSignature)
            };
        if (target == "account")
            certificate = certificate with
            {
                AccountSignature = Mutate(certificate.AccountSignature)
            };
        if (target == "context")
            context = context with
            {
                Reference = context.Reference + 1
            };
        if (target == "key")
            certificate = certificate with
            {
                AccountPublicKey = Mutate(certificate.AccountPublicKey)
            };

        Assert.NotNull(certificate.Validate(context));
    }

    static ImmutableArray<byte> Mutate(ImmutableArray<byte> bytes)
    {
        var data = bytes.ToArray();
        data[^1] ^= 1;
        return [.. data];
    }

    [Theory]
    [InlineData("channels", "channel_id", "channel_id")]
    [InlineData("groups", "group_id", "group_id")]
    public void Resource_identity_matches_fixed_vector(string file, string group, string expectedKey)
    {
        var vector = ProtocolVectors.Read(file).GetProperty(group);
        var input = vector.GetProperty("input");
        var creator = input.GetProperty("creator").GetString()!;
        var relay = input.GetProperty("relay_id").GetString()!;
        var nonce = ProtocolVectors.Decode(input.GetProperty("nonce").GetString()!);
        var actual = file == "channels" ? Identifiers.DeriveChannelId(creator, relay, nonce, TestNetwork.Context) : Identifiers.DeriveGroupId(creator, relay, nonce, TestNetwork.Context);

        Assert.Equal(vector.GetProperty("expected").GetProperty(expectedKey).GetString(), actual);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(9007199254740990, 9007199254740991)]
    public void Revision_advances_without_losing_integer_precision(long previous, long next)
    {
        Assert.Equal(next, AccountRoute.GetNextRevision(null, previous));
        Assert.Equal(next, AccountDeviceState.GetNextRevision(null, previous));
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(3, 4)]
    [InlineData(-1, 4)]
    [InlineData(9007199254740992, 4)]
    public void Invalid_revision_is_rejected(long requested, long known)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountRoute.GetNextRevision(requested, known));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountDeviceState.GetNextRevision(requested, known));
    }

    [Fact]
    public void Relay_descriptor_requires_signature_identity_and_unexpired_time()
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();

        Assert.Null(relay.Descriptor.Validate(TestNetwork.Context));
        Assert.NotNull((relay.Descriptor with { RelaySignature = Mutate(relay.Descriptor.RelaySignature) }).Validate(TestNetwork.Context));

        relay.Clock.Advance(TimeSpan.FromDays(5));

        Assert.Equal(ProtocolViolationKind.Time, relay.Descriptor.Validate(TestNetwork.Context)!.Kind);
    }

    [Fact]
    public void Device_authorization_uses_half_open_validity_interval()
    {
        using var account = new AccountSigner();
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        var signer = new DeviceSigner(account, clock);
        var state = new AccountDeviceState
        {
            Account = account.AccountId,
            AccountPublicKey = account.PublicKey,
            Certificates = [signer.Certificate],
            Revision = 1,
            AccountSignature = []
        };
        var device = signer.Certificate.GetDeviceId(TestNetwork.Context);

        Assert.Null(state.ValidateDeviceAuthorization(device, TestNetwork.Context));

        clock.Advance(TimeSpan.FromDays(30));

        Assert.Equal(ProtocolViolationKind.Time, state.ValidateDeviceAuthorization(device, TestNetwork.Context)!.Kind);
    }
}
