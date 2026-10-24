using Meshline.Identity;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Protocol;

public sealed class PushCertificateTests
{
    public static IEnumerable<object[]> Certificates() => Cases("device_certificates");
    public static IEnumerable<object[]> Tampering() => Cases("certificate_tampering");
    public static IEnumerable<object[]> GatewayUrls() => Cases("gateway_urls");

    static IEnumerable<object[]> Cases(string section) => ProtocolVectors.Read("push-notifications")
        .GetProperty(section).EnumerateArray().Select(value => new object[] { value.GetProperty("name").GetString()!, value.GetRawText() });

    [Theory]
    [MemberData(nameof(Certificates))]
    public void Certificate_matches_fixed_signatures_and_identity(string name, string json)
    {
        TestContext.Current.TestOutputHelper!.WriteLine(name);
        using var document = JsonDocument.Parse(json);
        var vector = document.RootElement;
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(vector.GetProperty("object").GetRawText())!;

        Assert.Null(certificate.Validate(TestNetwork.Context));
        Assert.Equal(vector.GetProperty("device_id").GetString(), certificate.GetDeviceId(TestNetwork.Context));
        AssertInput(vector.GetProperty("device_signing_input"), certificate.GetDeviceSigningInput(TestNetwork.Context));
        AssertInput(vector.GetProperty("account_signing_input"), certificate.GetAccountSigningInput(TestNetwork.Context));
        Assert.Equal(certificate.ToJson(), ProtocolModel.FromJson<DeviceCertificate>(certificate.ToJson())!.ToJson());
        Assert.DoesNotContain("push_gateway", certificate.AdditionalProperties.Keys);
        Assert.NotNull(certificate.Validate(TestNetwork.Context with { Reference = TestNetwork.Context.Reference + 1 }));
    }

    static void AssertInput(JsonElement expected, byte[] actual)
    {
        Assert.Equal(Convert.FromHexString(expected.GetProperty("utf8_hex").GetString()!), actual);
        Assert.Equal(expected.GetProperty("canonical_json").GetString(), Encoding.UTF8.GetString(actual));
        Assert.Equal(ProtocolVectors.Decode(expected.GetProperty("sha256").GetString()!["sha256:".Length..]), SHA256.HashData(actual));
    }

    [Theory]
    [MemberData(nameof(Tampering))]
    public void Gateway_and_unknown_properties_are_covered_by_both_signatures(string name, string json)
    {
        TestContext.Current.TestOutputHelper!.WriteLine(name);
        using var document = JsonDocument.Parse(json);
        var vector = document.RootElement;
        var source = ProtocolVectors.Read("push-notifications");
        foreach (var segment in vector.GetProperty("certificate_ref").GetString()!.Split('/').Skip(1))
            source = source.ValueKind == JsonValueKind.Array ? source[int.Parse(segment)] : source.GetProperty(segment);
        var original = ProtocolModel.FromJson<DeviceCertificate>(source.GetRawText())!;
        var node = JsonNode.Parse(source.GetRawText())!.AsObject();
        foreach (var field in vector.GetProperty("remove").EnumerateArray()) node.Remove(field.GetString()!);
        foreach (var field in vector.GetProperty("set").EnumerateObject()) node[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        var modified = ProtocolModel.FromJson<DeviceCertificate>(node.ToJsonString())!;

        Assert.Equal(vector.GetProperty("expected_device_signature_valid").GetBoolean(),
            Ed25519.Verify(modified.DeviceSignature.AsSpan(), modified.SigningPublicKey.AsSpan(), modified.GetDeviceSigningInput(TestNetwork.Context)));
        Assert.Equal(vector.GetProperty("expected_account_signature_valid").GetBoolean(),
            AccountAdapter.For(modified.Account).VerifySignature(modified.AccountPublicKey.AsSpan(), modified.GetAccountSigningInput(TestNetwork.Context), modified.AccountSignature.AsSpan()));
        Assert.Equal(ProtocolViolationKind.Signature, modified.Validate(TestNetwork.Context)!.Kind);
        Assert.Equal(original.GetDeviceId(TestNetwork.Context), modified.GetDeviceId(TestNetwork.Context));
    }

    [Theory]
    [MemberData(nameof(GatewayUrls))]
    public void Gateway_format_matches_protocol_vectors_without_rewriting(string name, string json)
    {
        TestContext.Current.TestOutputHelper!.WriteLine(name);
        using var document = JsonDocument.Parse(json);
        var vector = document.RootElement;
        var value = vector.GetProperty("value");
        var node = JsonNode.Parse(ProtocolVectors.Read("push-notifications").GetProperty("device_certificates")[0].GetProperty("object").GetRawText())!;
        node["push_gateway"] = JsonNode.Parse(value.GetRawText());
        if (value.ValueKind == JsonValueKind.Null)
        {
            Assert.Throws<JsonException>(() => ProtocolModel.FromJson<DeviceCertificate>(node.ToJsonString()));
            return;
        }
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(node.ToJsonString())!;
        var valid = vector.GetProperty("expected_format_valid").GetBoolean();

        Assert.Equal(valid ? ProtocolViolationKind.Signature : ProtocolViolationKind.Format, certificate.Validate(TestNetwork.Context)!.Kind);
        Assert.Equal(value.GetString(), certificate.PushGateway);
    }

    [Theory]
    [InlineData("HTTPS://push.example.com/callback")]
    [InlineData("https://@push.example.com/callback")]
    [InlineData("https://push.example.com:65536/callback")]
    [InlineData("https://push.example.com/\u0000")]
    [InlineData("https://push.example.com/\u0085")]
    public void Invalid_gateway_declaration_is_rejected(string gateway)
    {
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(ProtocolVectors.Read("push-notifications")
            .GetProperty("device_certificates")[0].GetProperty("object").GetRawText())!;
        Assert.Equal(ProtocolViolationKind.Format, (certificate with { PushGateway = gateway }).Validate(TestNetwork.Context)!.Kind);
    }

    [Fact]
    public void Gateway_length_counts_utf8_bytes_and_preserves_the_original_string()
    {
        var gateway = "https://PUSH.example.com:443/?q=" + new string('界', 160);
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(ProtocolVectors.Read("push-notifications")
            .GetProperty("device_certificates")[0].GetProperty("object").GetRawText())!;
        Assert.Equal(512, Encoding.UTF8.GetByteCount(gateway));
        certificate = certificate with { PushGateway = gateway };
        Assert.Equal(ProtocolViolationKind.Signature, certificate.Validate(TestNetwork.Context)!.Kind);
        Assert.Equal(gateway, ProtocolModel.FromJson<DeviceCertificate>(certificate.ToJson())!.PushGateway);
        Assert.Equal(ProtocolViolationKind.Format, (certificate with { PushGateway = gateway + "界" }).Validate(TestNetwork.Context)!.Kind);
    }
}
