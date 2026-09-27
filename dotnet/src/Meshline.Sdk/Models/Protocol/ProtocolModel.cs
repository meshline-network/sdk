using Meshline.Serialization;
using Meshline.Validation;
using System.Buffers;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Meshline.Models.Protocol;

/// <summary>
/// Provides protocol JSON serialization and an overridable validation entry point.
/// </summary>
/// <remarks>
/// Deserialization enforces JSON representation rules but does not call <see cref="Validate"/>. Call the relevant model validation and verify any required external signer or authorization evidence before trusting a received document.
/// </remarks>
public abstract record ProtocolModel : IJsonOnDeserialized
{
    const int MaxJsonDepth = 16;
    const long MaxSafeInteger = 9_007_199_254_740_991;
    static readonly JsonSerializerOptions _serializerOptions = CreateSerializerOptions();
    ImmutableDictionary<string, JsonElement> _additionalProperties = ImmutableDictionary<string, JsonElement>.Empty;
    Dictionary<string, JsonElement>? _extensionData;

    /// <summary>
    /// Unknown JSON properties retained for round-tripping, with cloned values and ordinal key comparison.
    /// </summary>
    [JsonIgnore]
    public ImmutableDictionary<string, JsonElement> AdditionalProperties
    {
        get => _additionalProperties;
        init => _additionalProperties = value.ToImmutableDictionary(static property => property.Key, static property => property.Value.Clone(), StringComparer.Ordinal);
    }

    [JsonInclude, JsonExtensionData]
    private Dictionary<string, JsonElement>? ExtensionData
    {
        get => _extensionData ?? (AdditionalProperties.IsEmpty ? null : AdditionalProperties.ToDictionary(static property => ProtocolStringValidator.Validate(property.Key), static property => property.Value, StringComparer.Ordinal));
        set => _extensionData = value;
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (_extensionData is null)
            return;

        _additionalProperties = _extensionData.ToImmutableDictionary(static property => property.Key, static property => property.Value.Clone(), StringComparer.Ordinal);
        _extensionData = null;
    }

    /// <summary>
    /// Deserializes protocol JSON with strict encoding, number, property, and discriminator rules.
    /// </summary>
    /// <typeparam name="T">The protocol model type to deserialize.</typeparam>
    /// <param name="json">The protocol JSON text to deserialize.</param>
    /// <returns>The deserialized model, or <see langword="null"/> for a JSON null value.</returns>
    /// <remarks>
    /// The returned model has not passed semantic or cryptographic validation. JSON null yields a null result. Invalid Unicode, duplicate properties, unsupported number representations, and excessive nesting are rejected by the protocol serializer.
    /// </remarks>
    /// <exception cref="JsonException">The JSON representation violates protocol serialization rules.</exception>
    /// <exception cref="ArgumentNullException">The <paramref name="json"/> argument is null.</exception>
    /// <exception cref="NotSupportedException">The requested model or one of its members has no supported JSON deserialization representation.</exception>
    public static T? FromJson<T>(string json) where T : ProtocolModel
    {
        try
        {
            using var document = JsonSerializer.Deserialize<JsonDocument>(json, _serializerOptions)!;
            ValidateInput(document.RootElement);
            return document.RootElement.Deserialize<T>(_serializerOptions);
        }
        catch (ArgumentException exception) when (exception.InnerException is EncoderFallbackException)
        {
            throw new JsonException("JSON strings must contain only Unicode scalar values.", exception);
        }
    }

    /// <summary>
    /// Serializes the model to canonical protocol JSON with ordinal property ordering.
    /// </summary>
    /// <returns>The canonical JSON representation of this model.</returns>
    /// <remarks>
    /// Output uses snake_case property names, canonical base64url byte strings, and ordinal object-key ordering. Serialization does not substitute for semantic or signature validation.
    /// </remarks>
    /// <exception cref="JsonException">The JSON representation violates protocol serialization rules.</exception>
    /// <exception cref="NotSupportedException">The runtime model or one of its members has no supported JSON serialization representation.</exception>
    public string ToJson()
    {
        using var document = SerializeToDocument();
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = CanonicalJsonEncoder.Instance }))
        {
            WriteSortedJson(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Provides the validation entry point for derived protocol models; the base implementation reports no violations.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public virtual ProtocolViolation? Validate(NetworkContext? context = null) => null;

    private protected JsonDocument SerializeToDocument(params string[] excludedProperties)
    {
        if (excludedProperties.Length == 0)
            return JsonSerializer.SerializeToDocument(this, GetType(), _serializerOptions);

        var typeInfo = _serializerOptions.TypeInfoResolver!.GetTypeInfo(GetType(), _serializerOptions)!;
        foreach (var excludedProperty in excludedProperties)
            typeInfo.Properties.Remove(typeInfo.Properties.Single(property => property.Name == excludedProperty));
        return JsonSerializer.SerializeToDocument(this, typeInfo);
    }

    static void ValidateInput(JsonElement value, int depth = 0)
    {
        if (depth > MaxJsonDepth)
            throw new JsonException($"JSON node depth must not exceed {MaxJsonDepth}.");

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    ValidateInput(property.Value, depth + 1);
                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ValidateInput(item, depth + 1);
                break;

            case JsonValueKind.String:
                _ = ReadString(value);
                break;

            case JsonValueKind.Number:
                _ = ReadInteger(value);
                break;
        }
    }

    static void WriteSortedJson(Utf8JsonWriter writer, JsonElement value)
    {
        if (writer.CurrentDepth > MaxJsonDepth)
            throw new JsonException($"JSON node depth must not exceed {MaxJsonDepth}.");

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteSortedObject(writer, value.EnumerateObject().Select(static property => (property.Name, property.Value)));
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteSortedJson(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.Number:
                writer.WriteNumberValue(ReadInteger(value));
                break;

            default:
                value.WriteTo(writer);
                break;
        }
    }

    private protected static void WriteSortedObject(Utf8JsonWriter writer, IEnumerable<(string Name, JsonElement Value)> properties)
    {
        writer.WriteStartObject();
        foreach (var (name, value) in properties.OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(name);
            WriteSortedJson(writer, value);
        }
        writer.WriteEndObject();
    }

    static string ReadString(JsonElement value)
    {
        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("JSON strings must contain only Unicode scalar values.", exception);
        }
    }

    static long ReadInteger(JsonElement value)
    {
        if (!value.TryGetInt64(out var number))
            throw new JsonException("Expected an integer in shortest decimal form.");

        if (number == 0 && value.GetRawText() == "-0")
            throw new JsonException("Negative zero is not a valid protocol integer.");

        if (number < -MaxSafeInteger || number > MaxSafeInteger)
            throw new JsonException("Protocol integers must be between -(2^53 - 1) and 2^53 - 1.");

        return number;
    }

    static JsonSerializerOptions CreateSerializerOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(ProtocolModel).IsAssignableFrom(typeInfo.Type))
                return;

            for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                var property = typeInfo.Properties[i];
                if (property.AttributeProvider is PropertyInfo member && member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always)
                {
                    typeInfo.Properties.RemoveAt(i);
                    continue;
                }

                ConfigureFieldUpdateProperty(property);

                if (property.IsRequired || property.IsExtensionData || property.Set is not { } setter
                    || property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
                    continue;

                property.ShouldSerialize = static (_, value) => value is not null;
                property.Set = (instance, value) => setter(instance, value ?? throw new JsonException($"The {property.Name} property cannot be null."));
            }

            var propertyNames = typeInfo.Properties.Where(static property => !property.IsExtensionData && property.Get is not null)
                .Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
            typeInfo.OnSerializing = instance =>
            {
                var model = (ProtocolModel)instance;
                if (model is TypedProtocolModel typed)
                    typed.CheckType();

                foreach (var name in model.AdditionalProperties.Keys)
                {
                    if (propertyNames.Contains(name))
                        throw new JsonException($"AdditionalProperties cannot contain the defined property '{name}'.");
                }
            };

            if (!typeof(TypedProtocolModel).IsAssignableFrom(typeInfo.Type))
                return;

            var discriminator = typeInfo.Properties.Single(static property => property.Name == "$type");
            discriminator.IsRequired = true;
            discriminator.Set = static (instance, value) =>
            {
                if (value is not string type || !string.Equals(((TypedProtocolModel)instance).Type, type, StringComparison.Ordinal))
                    throw new JsonException("The $type value does not match the requested protocol model.");
            };
        });

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            AllowDuplicateProperties = false,
            RespectNullableAnnotations = true,
            MaxDepth = MaxJsonDepth + 1,
            TypeInfoResolver = resolver
        };
        options.Converters.Add(new TypedProtocolModelConverterFactory());
        options.Converters.Add(new RpcResponseConverter());
        options.Converters.Add(new ProtocolStringConverter());
        options.Converters.Add(new Base64UrlConverter());
        options.Converters.Add(new FieldUpdateConverterFactory());
        options.Converters.Add(new ProtocolEnumConverter<ChannelStatus>());
        options.Converters.Add(new ProtocolEnumConverter<ContactRelationshipState>());
        options.Converters.Add(new ProtocolEnumConverter<MessageDeliveryState>());
        options.Converters.Add(new ProtocolEnumConverter<DeviceStatePublishStatus>());
        options.Converters.Add(new ProtocolEnumConverter<GroupInvitePolicy>());
        options.Converters.Add(new ProtocolEnumConverter<GroupRole>());
        options.Converters.Add(new ProtocolEnumConverter<GroupStatus>());
        options.Converters.Add(new ProtocolEnumConverter<SessionMode>());
        options.MakeReadOnly();
        return options;
    }

    static void ConfigureFieldUpdateProperty(JsonPropertyInfo property)
    {
        var type = property.PropertyType;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(FieldUpdate<>))
            return;

        property.ShouldSerialize = typeof(ProtocolModel)
            .GetMethod(nameof(ShouldSerializeFieldUpdate), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(type.GetGenericArguments())
            .CreateDelegate<Func<object, object?, bool>>();
    }

    static bool ShouldSerializeFieldUpdate<T>(object _, object? value) where T : notnull => value is FieldUpdate<T> update && (update.IsSpecified || update.IsDeleted);
}
