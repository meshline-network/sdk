namespace Meshline.Models.Client;

/// <summary>
/// Reports synchronization progress, blocking information, and retention gaps for a resource.
/// </summary>
public sealed class ResourceSyncStatus
{
    /// <summary>
    /// The resource identifier associated with the synchronization or error, when available.
    /// </summary>
    public required string Resource { get; init; }
    /// <summary>
    /// The current synchronization progress state.
    /// </summary>
    public required ResourceSyncState State { get; init; }
    /// <summary>
    /// The time synchronization last completed successfully, or <see langword="null"/> before any completion.
    /// </summary>
    public DateTimeOffset? LastSynchronizedAt { get; init; }
    /// <summary>
    /// The reason synchronization is blocked, when applicable.
    /// </summary>
    public ResourceSyncBlockReason? BlockReason { get; init; }
    /// <summary>
    /// The error associated with this result or diagnostic.
    /// </summary>
    public Exception? Error { get; init; }
    /// <summary>
    /// Whether retained history omits entries needed to span the requested synchronization range.
    /// </summary>
    public bool HasRetentionGap { get; init; }
}
