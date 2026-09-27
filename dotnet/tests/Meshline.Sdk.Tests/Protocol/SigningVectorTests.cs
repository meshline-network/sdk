using Meshline.Models.Protocol;
using Meshline.Models;
using Meshline.Tests.Support;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace Meshline.Tests.Protocol;

public sealed class SigningVectorTests
{
    public static IEnumerable<object[]> Contacts() => Rows(ProtocolVectors.Read("contacts").GetProperty("signing").GetProperty("verification_cases"));
    public static IEnumerable<object[]> Channels() => Rows(ProtocolVectors.Read("channels").GetProperty("channel_write_cases"));
    public static IEnumerable<object[]> Management() => Rows(ProtocolVectors.Read("groups").GetProperty("management_chain").GetProperty("chain"));
    static IEnumerable<object[]> Rows(JsonElement value) => value.EnumerateArray().Select(row => new object[] { row.GetRawText() });
    [Theory, MemberData(nameof(Contacts))]
    public void Contact_grant_signatures_bind_body_extensions_but_exclude_signature_map(string json)
    {
        using var document = JsonDocument.Parse(json);
        var test = document.RootElement;
        var vector = ProtocolVectors.Read("contacts").GetProperty("signing").GetProperty("vector");
        var node = JsonNode.Parse(vector.GetProperty("grant").GetRawText())!;
        if (test.TryGetProperty("grant_overrides", out var changes))
            foreach (var change in changes.EnumerateObject())
                node[change.Name] = JsonNode.Parse(change.Value.GetRawText());
        if (test.TryGetProperty("grant_remove_fields", out var removals))
            foreach (var key in removals.EnumerateArray())
                node.AsObject().Remove(key.GetString()!);
        var expected = test.GetProperty("expected_signature_valid").GetBoolean();
        ContactGrant grant;
        try
        {
            grant = ProtocolModel.FromJson<ContactGrant>(node.ToJsonString())!;
        }
        catch (JsonException)
        {
            Assert.False(expected);

            return;
        }

        var context = test.TryGetProperty("network_context", out var network) ? NetworkContext.Parse(network.GetString()!) : TestNetwork.Context;
        var bytes = grant.GetSigningInput(context);
        if (test.GetProperty("name").GetString() == "original_body")
            Assert.Equal(Convert.FromHexString(vector.GetProperty("signing_input_utf8_hex").GetString()!), bytes);
        var publicKey = ProtocolVectors.Decode((test.TryGetProperty("public_key", out var keyValue) ? keyValue : vector.GetProperty("public_key")).GetString()!);
        var signature = ProtocolVectors.Decode((test.TryGetProperty("signature", out var signatureValue) ? signatureValue : vector.GetProperty("signature")).GetString()!);

        Assert.Equal(expected, Ed25519.Verify(signature, publicKey, bytes));
    }

    [Theory, MemberData(nameof(Channels))]
    public void Channel_writes_match_fixed_signing_inputs_and_signatures(string json)
    {
        using var document = JsonDocument.Parse(json);
        var vector = document.RootElement;
        var request = vector.GetProperty("request");
        var model = ProtocolModel.FromJson<TypedProtocolModel>(request.GetRawText())!;
        var bytes = model.GetSigningInput(TestNetwork.Context, "device_signature");

        Assert.Null(model.Validate(TestNetwork.Context));
        Assert.Equal(Convert.FromHexString(vector.GetProperty("signing_input_utf8_hex").GetString()!), bytes);
        Assert.True(Ed25519.Verify(
            ProtocolVectors.Decode(request.GetProperty("device_signature").GetString()!),
            ProtocolVectors.Decode(ProtocolVectors.Read("channels").GetProperty("channel_signer").GetProperty("public_key").GetString()!),
            bytes));
    }

    [Theory, MemberData(nameof(Management))]
    public void Group_management_chain_matches_independent_bytes_hashes_and_signatures(string json)
    {
        using var document = JsonDocument.Parse(json);
        var vector = document.RootElement;
        var value = vector.GetProperty("event");
        var entry = ProtocolModel.FromJson<GroupEvent>(value.GetRawText())!;
        var bytes = entry.Payload.GetSigningInput(TestNetwork.Context, "device_signature");

        Assert.Equal(Convert.FromHexString(vector.GetProperty("signing_input_utf8_hex").GetString()!), bytes);

        if (vector.TryGetProperty("hash_input_utf8_hex", out var expectedHashInput))
        {
            var hashInput = entry.Payload.GetSigningInput(TestNetwork.Context);

            Assert.Equal(Convert.FromHexString(expectedHashInput.GetString()!), hashInput);
            Assert.Equal(vector.GetProperty("management_hash").GetString(), "sha256:" + Base64Url.EncodeToString(SHA256.HashData(hashInput)));
        }
        else
            Assert.IsType<GroupMessageEnvelope>(entry.Payload);
        var actor = ProtocolVectors.Read("groups").GetProperty("management_chain").GetProperty("actors").EnumerateArray().Single(actor => actor.GetProperty("device_id").GetString() == entry.SignerDeviceId);
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(actor.GetProperty("certificate").GetRawText())!;

        Assert.Null(certificate.Validate(TestNetwork.Context));
        Assert.True(Ed25519.Verify(ProtocolVectors.Decode(value.GetProperty("payload").GetProperty("device_signature").GetString()!), certificate.SigningPublicKey.AsSpan(), bytes));
    }
}
