using Meshline.Models;
using Meshline.Models.Protocol;
using Meshline.Serialization;
using Meshline.Tests.Support;
using Meshline.Validation;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Tests.Protocol;

public sealed class SerializationTests
{
    public sealed record Document : ProtocolModel;
    public sealed record UpdateDocument : ProtocolModel
    {
        public FieldUpdate<string> Name { get; init; }
    }

    public static IEnumerable<object[]> CanonicalCases() => Rows("canonical_json", "vectors");
    public static IEnumerable<object[]> Rejections() => Rows("canonical_json", "rejection_vectors");
    public static IEnumerable<object[]> Base64Cases() => Rows("base64url", "cases");
    public static IEnumerable<object[]> NetworkCases() => Rows("network_binding", "format_cases");
    static IEnumerable<object[]> Rows(string group, string list) => ProtocolVectors.Read("common").GetProperty(group).GetProperty(list).EnumerateArray().Select(value => new object[] { value.GetRawText() });
    [Theory, MemberData(nameof(CanonicalCases))]
    public void Canonical_json_matches_independent_bytes_and_hash(string json)
    {
        using var source = JsonDocument.Parse(json);
        var vector = source.RootElement;
        var input = vector.GetProperty("input_json").GetString()!;
        var expected = vector.GetProperty("expected");

        Assert.Equal(Convert.FromHexString(vector.GetProperty("input_utf8_hex").GetString()!), Encoding.UTF8.GetBytes(input));

        var document = ProtocolModel.FromJson<Document>(input)!;
        if (vector.GetProperty("operation").GetString() == "network_bound_json")
            document = document with
            {
                AdditionalProperties = document.AdditionalProperties.Add("$context", JsonSerializer.SerializeToElement(TestNetwork.Context.ToString()))
            };
        var actual = document.ToJson();

        Assert.Equal(expected.GetProperty("canonical_json").GetString(), actual);
        Assert.Equal(Convert.FromHexString(expected.GetProperty("utf8_hex").GetString()!), Encoding.UTF8.GetBytes(actual));
        Assert.Equal(expected.GetProperty("sha256_hex").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actual))));
    }

    [Theory, MemberData(nameof(Rejections))]
    public void Invalid_canonical_inputs_are_rejected(string json)
    {
        using var source = JsonDocument.Parse(json);
        var vector = source.RootElement;

        Assert.Throws<JsonException>(() => ProtocolModel.FromJson<Document>(vector.GetProperty("input_json").GetString()!)!.ToJson());
    }

    [Theory, MemberData(nameof(Base64Cases))]
    public void Base64url_requires_canonical_encoding(string json)
    {
        using var source = JsonDocument.Parse(json);
        var vector = source.RootElement;
        var value = vector.GetProperty("input").GetString()!;
        var accepted = vector.GetProperty("expected_accepted").GetBoolean();
        if (vector.TryGetProperty("prefix", out var prefixValue) && prefixValue.GetString() is { Length: > 0 } prefix)
        {
            var valid = prefix switch
            {
                "dev_" => Identifiers.ValidateDeviceId(value) is null,
                "msg_" => Identifiers.ValidateMessageId(value) is null,
                "chan_" => Identifiers.ValidateChannelId(value) is null,
                "grp_" => Identifiers.ValidateGroupId(value) is null,
                "inv_" => Identifiers.ValidateInviteId(value) is null,
                "sha256:" => new ContentReference
                {
                    Uri = "https://content.test/a",
                    Hash = value,
                    ContentType = "application/octet-stream",
                    Size = 0
                }.Validate() is null,
                _ => throw new InvalidOperationException(prefix)
            };

            Assert.Equal(accepted, valid);

            if (!accepted)
                return;
            value = value[prefix.Length..];
        }

        Assert.Equal(accepted, Base64UrlValidator.IsValid(value));

        var options = new JsonSerializerOptions();
        options.Converters.Add(new Base64UrlConverter());
        if (accepted)
        {
            var bytes = JsonSerializer.Deserialize<ImmutableArray<byte>>(JsonSerializer.Serialize(value), options);

            Assert.Equal(Convert.FromHexString(vector.GetProperty("decoded_hex").GetString()!), bytes.ToArray());
        }
        else
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ImmutableArray<byte>>(JsonSerializer.Serialize(value), options));
    }

    [Theory, MemberData(nameof(NetworkCases))]
    public void Network_context_format_is_canonical(string json)
    {
        using var source = JsonDocument.Parse(json);
        var vector = source.RootElement;

        Assert.Equal(
            vector.GetProperty("expected_accepted").GetBoolean(),
            NetworkContext.TryParse(vector.GetProperty("input").ValueKind == JsonValueKind.String ? vector.GetProperty("input").GetString() : null, out _));
    }

    [Theory]
    [InlineData("{}", "{}")]
    [InlineData("{\"name\":null}", "{\"name\":null}")]
    [InlineData("{\"name\":\"updated\"}", "{\"name\":\"updated\"}")]
    public void Field_updates_preserve_absent_null_and_value(string json, string expected) => Assert.Equal(expected, ProtocolModel.FromJson<UpdateDocument>(json)!.ToJson());
    [Theory]
    [InlineData("{\"$type\":\"meshline.message.direct\",\"body\":{\"content_type\":\"text/plain\",\"text\":\"hello\"}}", typeof(DirectMessage))]
    [InlineData("{\"$type\":\"vendor.future\",\"extra\":42}", typeof(TypedProtocolModel))]
    public void Type_dispatch_and_unknown_properties_roundtrip(string json, Type type)
    {
        var value = ProtocolModel.FromJson<TypedProtocolModel>(json)!;

        Assert.IsType(type, value);

        using var expected = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(value.ToJson());

        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    [Theory]
    [InlineData("{\"$type\":\"wrong\"}")]
    [InlineData("{\"body\":null}")]
    public void Known_models_require_correct_type_and_nonnull_fields(string json) => Assert.Throws<JsonException>(() => ProtocolModel.FromJson<DirectMessage>(json));
    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":null}", true)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"error\":{\"code\":-32600,\"message\":\"bad\"}}", false)]
    public void Rpc_response_dispatches_success_and_failure(string json, bool success)
    {
        var response = ProtocolModel.FromJson<RpcResponse>(json);

        Assert.Equal(success, response is RpcSuccess);
        Assert.Equal(!success, response is RpcFailure);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"1\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":null,\"error\":{\"code\":1,\"message\":\"bad\"}}")]
    public void Ambiguous_rpc_response_is_rejected(string json) => Assert.Throws<JsonException>(() => ProtocolModel.FromJson<RpcResponse>(json));
    [Theory]
    [InlineData("\"Device\"")]
    [InlineData("0")]
    [InlineData("\"future\"")]
    public void Unknown_or_noncanonical_session_mode_is_rejected(string mode) => Assert.Throws<JsonException>(() => ProtocolModel.FromJson<SessionCredentials>("{\"token\":\"t\",\"expires_at\":42,\"mode\":" + mode + "}"));
    [Fact]
    public void Snapshot_files_match_recorded_hashes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Vectors");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
            Assert.Equal(file.Value.GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(path, file.Name)))));
    }
}
