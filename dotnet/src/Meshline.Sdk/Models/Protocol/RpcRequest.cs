using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Models.Protocol;

/// <summary>
/// Represents a JSON-RPC request or notification.
/// </summary>
public sealed record RpcRequest : ProtocolModel
{
    /// <summary>
    /// The JSON-RPC protocol version, which must be <c>2.0</c>.
    /// </summary>
    [JsonRequired]
    [SuppressMessage("Performance", "CA1822")]
    public string Jsonrpc
    {
        get => "2.0";
        init
        {
            if (value != "2.0")
                throw new JsonException("The jsonrpc value must be '2.0'.");
        }
    }

    /// <summary>
    /// The request identifier, or <see langword="null"/> for a notification that expects no response.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// The JSON-RPC method name.
    /// </summary>
    public required string Method { get; init; }
    /// <summary>
    /// Optional named JSON-RPC parameters, with JSON values cloned on assignment.
    /// </summary>
    public ImmutableDictionary<string, JsonElement>? Params { get; init => field = value?.ToImmutableDictionary(static property => property.Key, static property => property.Value.Clone(), StringComparer.Ordinal); }

    /// <summary>
    /// Whether this request omits an identifier and therefore expects no response.
    /// </summary>
    [JsonIgnore]
    [MemberNotNullWhen(false, nameof(Id))]
    public bool IsNotification => Id is null;
}
