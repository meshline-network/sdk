using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Serialization;

sealed class ProtocolEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly FrozenDictionary<T, string> Names = Enum.GetValues<T>()
        .ToFrozenDictionary(static value => value, static value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()));

    private static readonly FrozenDictionary<string, T> Values = Names
        .ToFrozenDictionary(static pair => pair.Value, static pair => pair.Key, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Values.TryGetValue(reader.GetString()!, out var value))
            throw new JsonException($"Invalid {typeof(T).Name} string value.");

        return value;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Names.TryGetValue(value, out var name))
            throw new JsonException($"Undefined {typeof(T).Name} value.");

        writer.WriteStringValue(name);
    }
}
