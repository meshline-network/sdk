namespace Meshline.Models.Protocol;

/// <summary>
/// Represents a failed JSON-RPC response.
/// </summary>
public sealed record RpcFailure : RpcResponse
{
    /// <summary>
    /// The matching request identifier, or <see langword="null"/> if the relay could not identify the request.
    /// </summary>
    public required string? Id { get; init; }
    /// <summary>
    /// The error associated with this result or diagnostic.
    /// </summary>
    public required RpcError Error { get; init; }
}
