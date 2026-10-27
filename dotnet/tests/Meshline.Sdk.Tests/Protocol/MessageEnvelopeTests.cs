using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Protocol;

public sealed class MessageEnvelopeTests
{
    public static IEnumerable<object[]> Variants() => Cases("variants");
    public static IEnumerable<object[]> Fields() => Cases("field_cases");
    public static IEnumerable<object[]> Tampering() => Cases("signature_tampering");
    public static IEnumerable<object[]> Retries() => Cases("retry_cases");

    static IEnumerable<object[]> Cases(string section) =>
        new[] { "message-encryption", "groups" }.SelectMany(file =>
            ProtocolVectors.Read(file).GetProperty("notification").GetProperty(section).EnumerateArray()
                .Select(test => new object[] { file, test.GetRawText() }));

    [Theory, MemberData(nameof(Variants))]
    public void Envelopes_preserve_field_presence_and_match_fixed_signatures(string file, string json)
    {
        var root = ProtocolVectors.Read(file);
        var vectors = root.GetProperty("notification");
        using var test = JsonDocument.Parse(json);
        var expected = ProtocolVectors.Resolve(root, test.RootElement, "envelope");
        var envelope = ProtocolModel.FromJson<TypedProtocolModel>(expected.GetRawText())!;
        Assert.Null(envelope.Validate(TestNetwork.Context));
        Assert.DoesNotContain("notify", envelope.AdditionalProperties.Keys);
        Assert.Equal(expected.TryGetProperty("notify", out var notify) ? notify.GetBoolean() : (bool?)null, Notify(envelope));
        Assert.Equal(test.RootElement.GetProperty("expected_allows_push").GetBoolean(), Notify(envelope) == true);
        using var serialized = JsonDocument.Parse(envelope.ToJson());
        Assert.True(JsonElement.DeepEquals(expected, serialized.RootElement));
        var bytes = envelope.GetSigningInput(TestNetwork.Context, "device_signature");
        Assert.Equal(Convert.FromHexString(ProtocolVectors.Resolve(root, test.RootElement, "signing_input_utf8_hex").GetString()!), bytes);
        var privateKey = ProtocolVectors.Decode(ProtocolVectors.Resolve(root, vectors, "signing_private_key").GetString()!);
        var publicKey = ProtocolVectors.Decode(ProtocolVectors.Resolve(root, vectors, "signing_public_key").GetString()!);
        var signature = ProtocolVectors.Decode(expected.GetProperty("device_signature").GetString()!);
        var actual = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(privateKey, bytes, actual);
        Assert.Equal(signature, actual);
        Assert.True(Ed25519.Verify(signature, publicKey, bytes));
    }

    [Theory, MemberData(nameof(Fields))]
    public void Envelope_field_types_follow_protocol_vectors(string file, string json)
    {
        var root = ProtocolVectors.Read(file);
        using var test = JsonDocument.Parse(json);
        var candidate = Mutate(ProtocolVectors.Resolve(root, root.GetProperty("notification"), "base_envelope"), test.RootElement);
        if (!test.RootElement.GetProperty("expected_format_valid").GetBoolean())
        {
            Assert.Throws<JsonException>(() => ProtocolModel.FromJson<TypedProtocolModel>(candidate.ToJsonString()));
            return;
        }
        var envelope = ProtocolModel.FromJson<TypedProtocolModel>(candidate.ToJsonString())!;
        Assert.Null(envelope.Validate(TestNetwork.Context));
        Assert.Equal(test.RootElement.GetProperty("expected_allows_push").GetBoolean(), Notify(envelope) == true);
    }

    [Theory, MemberData(nameof(Tampering))]
    public void Changing_notification_intent_invalidates_the_original_signature(string file, string json)
    {
        var root = ProtocolVectors.Read(file);
        using var test = JsonDocument.Parse(json);
        var candidate = Mutate(ProtocolVectors.Resolve(root, test.RootElement, "envelope"), test.RootElement);
        var envelope = ProtocolModel.FromJson<TypedProtocolModel>(candidate.ToJsonString())!;
        Assert.Null(envelope.Validate(TestNetwork.Context));
        var signature = ProtocolVectors.Decode(candidate["device_signature"]!.GetValue<string>());
        var publicKey = ProtocolVectors.Decode(ProtocolVectors.Resolve(root, root.GetProperty("notification"), "signing_public_key").GetString()!);
        Assert.Equal(test.RootElement.GetProperty("expected_signature_valid").GetBoolean(),
            Ed25519.Verify(signature, publicKey, envelope.GetSigningInput(TestNetwork.Context, "device_signature")));
    }

    [Theory, MemberData(nameof(Retries))]
    public void Canonical_envelope_comparison_preserves_retry_identity(string file, string json)
    {
        var root = ProtocolVectors.Read(file);
        using var test = JsonDocument.Parse(json);
        var accepted = ProtocolModel.FromJson<TypedProtocolModel>(ProtocolVectors.Resolve(root, test.RootElement, "accepted_envelope").GetRawText())!;
        var retry = ProtocolModel.FromJson<TypedProtocolModel>(ProtocolVectors.Resolve(root, test.RootElement, "retry_envelope").GetRawText())!;
        Assert.Equal(test.RootElement.GetProperty("expected_result").GetString() == "original_result", accepted.ToJson() == retry.ToJson());
    }

    static bool? Notify(TypedProtocolModel model) => model switch
    {
        MessageEnvelope message => message.Notify,
        GroupMessageEnvelope message => message.Notify,
        _ => throw new InvalidOperationException(model.Type)
    };

    static JsonObject Mutate(JsonElement envelope, JsonElement test)
    {
        var node = JsonNode.Parse(envelope.GetRawText())!.AsObject();
        if (test.TryGetProperty("remove", out var remove))
            foreach (var field in remove.EnumerateArray()) node.Remove(field.GetString()!);
        if (test.TryGetProperty("set", out var set))
            foreach (var field in set.EnumerateObject()) node[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        return node;
    }
}
