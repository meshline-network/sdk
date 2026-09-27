using Meshline.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Serialization;

internal sealed class FieldUpdateConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(FieldUpdate<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;

    private sealed class Converter<T> : JsonConverter<FieldUpdate<T>> where T : notnull
    {
        public override FieldUpdate<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return FieldUpdate<T>.Delete;

            var value = JsonSerializer.Deserialize<T>(ref reader, options)
                ?? throw new JsonException("A field update value cannot be null.");

            return new FieldUpdate<T>(value);
        }

        public override void Write(Utf8JsonWriter writer, FieldUpdate<T> value, JsonSerializerOptions options)
        {
            if (value.IsDeleted)
            {
                writer.WriteNullValue();
                return;
            }

            if (!value.IsSpecified)
                throw new JsonException("An unspecified field update must be omitted from its containing object.");
            if (value.Value is null)
                throw new JsonException("Use FieldUpdate<T>.Delete to delete a field.");

            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
