namespace Meshline.Models.Client;

/// <summary>
/// Reports synchronization progress, blocking information, and retention gaps for a resource.
/// </summary>
public sealed class ResourceSyncStatus
{
    /// <summary>
    /// The relay ID for account messages, group ID for groups, or channel ID for channels.
    /// </summary>
    public required string Resource { get; init; }
    /// <summary>
    /// The current synchronization progress state.
    /// </summary>
    public required ResourceSyncState State { get; init; }
    /// <summary>
    /// The time a full pass last completed successfully in this component instance, or <see langword="null"/> before any completion. It is not restored on restart.
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
    /// Whether a retention gap has been observed. This is independent of CaughtUp; false does not guarantee complete history.
    /// </summary>
    public bool HasRetentionGap { get; init; }
}
