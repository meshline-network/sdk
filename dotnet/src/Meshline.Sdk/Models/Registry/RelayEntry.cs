namespace Meshline.Models.Registry;

/// <summary>
/// Describes a relay registration returned by the registry integration.
/// </summary>
public sealed record RelayEntry
{
    /// <summary>
    /// The relay's lowercase Neo script hash, including the <c>0x</c> prefix.
    /// </summary>
    public required string RelayId { get; init; }
    /// <summary>
    /// The relay endpoint recorded in the registry.
    /// </summary>
    public required string Endpoint { get; init; }
    /// <summary>
    /// The relay's registry status.
    /// </summary>
    public required RelayStatus Status { get; init; }
    /// <summary>
    /// The latest registry creation, endpoint update, or status update time, in Unix milliseconds.
    /// </summary>
    public required ulong UpdatedAt { get; init; }
}
