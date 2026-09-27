using Meshline.Validation;
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Serialization;

sealed class Base64UrlConverter : JsonConverter<ImmutableArray<byte>>
{
    public override ImmutableArray<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Binary data must be a base64url string.");

        var encoded = reader.GetString()!;
        if (!Base64UrlValidator.IsValid(encoded))
            throw new JsonException("Invalid canonical base64url string.");

        try
        {
            return ImmutableCollectionsMarshal.AsImmutableArray(Base64Url.DecodeFromChars(encoded));
        }
        catch (FormatException exception)
        {
            throw new JsonException("Invalid canonical base64url string.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, ImmutableArray<byte> value, JsonSerializerOptions options)
    {
        if (value.IsDefault)
            throw new JsonException("An uninitialized byte array cannot be serialized.");

        writer.WriteStringValue(Base64Url.EncodeToString(value.AsSpan()));
    }
}
