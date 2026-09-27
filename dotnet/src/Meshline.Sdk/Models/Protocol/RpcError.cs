using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a JSON-RPC error number, message, and optional structured details.
/// </summary>
public sealed record RpcError : ProtocolModel
{
    static readonly FrozenDictionary<long, string> _errorCodes = RelayError.RpcCodes.ToFrozenDictionary(static pair => pair.Value, static pair => pair.Key);

    /// <summary>
    /// The numeric JSON-RPC error code.
    /// </summary>
    public required long Code { get; init; }
    /// <summary>
    /// The relay's human-readable JSON-RPC error description.
    /// </summary>
    public required string Message { get; init; }
    /// <summary>
    /// Optional structured error details, with JSON values cloned on assignment.
    /// </summary>
    public ImmutableDictionary<string, JsonElement>? Data { get; init => field = value?.ToImmutableDictionary(static property => property.Key, static property => property.Value.Clone(), StringComparer.Ordinal); }

    /// <summary>
    /// Maps the JSON-RPC numeric code to a symbolic relay error code when known.
    /// </summary>
    /// <returns>The known symbolic error code, or <see langword="null"/> for an unmapped number.</returns>
    public string? GetRelayErrorCode() =>
        _errorCodes.GetValueOrDefault(Code);
}
