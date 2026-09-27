using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a symbolic relay error code, diagnostic message, and optional structured details.
/// </summary>
public sealed record RelayError : ProtocolModel
{
    internal static readonly FrozenDictionary<string, long> RpcCodes = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["bad_request"] = -32602,
        ["method_not_found"] = -32601,
        ["internal_error"] = -32603,
        ["unauthorized"] = -32001,
        ["forbidden"] = -32003,
        ["invalid_signature"] = -32004,
        ["device_unknown"] = -32005,
        ["clock_skew"] = -32006,
        ["not_found"] = -32010,
        ["state_conflict"] = -32011,
        ["stale_state"] = -32012,
        ["invalid_state"] = -32013,
        ["message_expired"] = -32014,
        ["request_too_large"] = -32015,
        ["target_not_local"] = -32020,
        ["route_stale"] = -32021,
        ["route_not_found"] = -32022,
        ["bad_gateway"] = -32023,
        ["rate_limited"] = -32030,
        ["temporarily_unavailable"] = -32031
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The symbolic protocol error code, such as <c>rate_limited</c>.
    /// </summary>
    public required string Code { get; init; }
    /// <summary>
    /// The relay's human-readable error description.
    /// </summary>
    public required string Message { get; init; }
    /// <summary>
    /// Optional structured error details, with JSON values cloned on assignment.
    /// </summary>
    public ImmutableDictionary<string, JsonElement>? Data { get; init => field = value?.ToImmutableDictionary(static property => property.Key, static property => property.Value.Clone(), StringComparer.Ordinal); }

    /// <summary>
    /// Maps the symbolic relay error code to its JSON-RPC numeric code when known.
    /// </summary>
    /// <returns>The known JSON-RPC error number, or <see langword="null"/> for an unknown symbolic code.</returns>
    public long? GetRpcErrorCode() =>
        RpcCodes.TryGetValue(Code, out var code) ? code : null;

    /// <summary>
    /// Reads a nonnegative retry delay from a rate-limit error, in seconds.
    /// </summary>
    /// <returns>The retry delay in seconds, or <see langword="null"/> when no applicable hint is present.</returns>
    /// <exception cref="JsonException">The applicable retry hint is not a nonnegative integer.</exception>
    public long? GetRetryAfter()
    {
        if (Code != "rate_limited" || Data is null || !Data.TryGetValue("retry_after", out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds) || seconds < 0)
            throw new JsonException("The retry_after hint must be a nonnegative integer.");

        return seconds;
    }

    /// <summary>
    /// Determines whether the relay error proves that the submitted operation was rejected.
    /// </summary>
    /// <returns><see langword="true"/> if the error is a known definitive rejection; otherwise, <see langword="false"/>.</returns>
    public bool IsDefinitiveRejection() => Code is "bad_request" or "method_not_found" or "unauthorized" or "forbidden" or "invalid_signature"
        or "device_unknown" or "clock_skew" or "not_found" or "state_conflict" or "stale_state" or "invalid_state" or "message_expired" or "request_too_large"
        or "target_not_local" or "route_stale" or "route_not_found" or "rate_limited";
}
