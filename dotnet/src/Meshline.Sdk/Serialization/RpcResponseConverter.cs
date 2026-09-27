using Meshline.Models.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Serialization;

internal sealed class RpcResponseConverter : JsonConverter<RpcResponse>
{
    public override RpcResponse? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonSerializer.Deserialize<JsonDocument>(ref reader, options)!;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A JSON-RPC response must be an object.");

        var hasResult = root.TryGetProperty("result", out _);
        var hasError = root.TryGetProperty("error", out _);
        if (hasResult == hasError)
            throw new JsonException("A JSON-RPC response must contain exactly one of result or error.");

        return hasResult
            ? root.Deserialize<RpcSuccess>(options)
            : root.Deserialize<RpcFailure>(options);
    }

    public override void Write(Utf8JsonWriter writer, RpcResponse value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
