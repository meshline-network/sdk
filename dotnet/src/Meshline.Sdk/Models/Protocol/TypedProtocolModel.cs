using Meshline.Serialization;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Models.Protocol;

/// <summary>
/// Adds a protocol type discriminator and network-bound signing input to a protocol model.
/// </summary>
public record TypedProtocolModel : ProtocolModel
{
    /// <summary>
    /// The protocol discriminator serialized as <c>$type</c>.
    /// </summary>
    [JsonPropertyName("$type")]
    public string Type { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="TypedProtocolModel"/>.
    /// </summary>
    /// <param name="type">The protocol discriminator to serialize as <c>$type</c>.</param>
    public TypedProtocolModel(string type) => Type = type;

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <param name="excludedProperties">Serialized property names to omit from the signing input, typically signature fields.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    /// <exception cref="InvalidOperationException">An excluded property name does not identify exactly one serialized property.</exception>
    internal protected byte[] GetSigningInput(NetworkContext context, params string[] excludedProperties)
    {
        using var document = SerializeToDocument(excludedProperties);
        var payload = document.RootElement;
        if (payload.TryGetProperty("$context", out _))
            throw new JsonException("A protocol object must not contain a root $context property.");

        var properties = payload.EnumerateObject()
            .Select(static property => (property.Name, property.Value))
            .Append(("$context", JsonSerializer.SerializeToElement(context.ToString())));
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = CanonicalJsonEncoder.Instance }))
        {
            WriteSortedObject(writer, properties);
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal void CheckType()
    {
        if (Type is null)
            throw new JsonException("The $type value must be a string.");
        if (AdditionalProperties.ContainsKey("$type"))
            throw new JsonException("AdditionalProperties cannot contain $type.");
    }
}
