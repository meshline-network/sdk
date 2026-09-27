using Meshline.Models.Protocol;
using Meshline.Validation;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Serialization;

internal sealed class TypedProtocolModelConverterFactory : JsonConverterFactory
{
    private static readonly FrozenDictionary<string, Type> Types = CreateTypes();

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(TypedProtocolModel) || typeToConvert.IsAbstract && typeof(TypedProtocolModel).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private static FrozenDictionary<string, Type> CreateTypes()
    {
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in typeof(TypedProtocolModel).Assembly.GetTypes())
        {
            if (!type.IsVisible || type.IsAbstract || !type.IsSubclassOf(typeof(TypedProtocolModel)))
                continue;

            var model = (TypedProtocolModel)Activator.CreateInstance(type)!;
            var typeName = model.Type;

            if (!types.TryAdd(typeName, type))
                throw new InvalidOperationException($"Protocol models '{types[typeName].FullName}' and '{type.FullName}' declare the same type name '{typeName}'.");
        }

        return types.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed class Converter<T> : JsonConverter<T> where T : TypedProtocolModel
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonSerializer.Deserialize<JsonDocument>(ref reader, options)!;
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("$type", out var discriminator) || discriminator.ValueKind != JsonValueKind.String)
                throw new JsonException("A protocol model must contain a string $type property.");

            var type = discriminator.GetString()!;
            var modelType = Types.GetValueOrDefault(type);
            if (modelType is not null)
            {
                if (!typeToConvert.IsAssignableFrom(modelType))
                    throw new JsonException("The $type value does not belong to the requested protocol model hierarchy.");

                return (T)root.Deserialize(modelType, options)!;
            }

            if (typeToConvert != typeof(TypedProtocolModel))
                throw new JsonException("The $type value is not supported by the requested protocol model hierarchy.");

            return (T)new TypedProtocolModel(type)
            {
                AdditionalProperties = root.EnumerateObject()
                    .Where(static property => property.Name != "$type")
                    .ToImmutableDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal)
            };
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value.GetType() != typeof(TypedProtocolModel))
            {
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                return;
            }

            value.CheckType();
            writer.WriteStartObject();
            writer.WriteString("$type", ProtocolStringValidator.Validate(value.Type));
            foreach (var property in value.AdditionalProperties)
            {
                writer.WritePropertyName(ProtocolStringValidator.Validate(property.Key));
                JsonSerializer.Serialize(writer, property.Value, options);
            }
            writer.WriteEndObject();
        }
    }
}
