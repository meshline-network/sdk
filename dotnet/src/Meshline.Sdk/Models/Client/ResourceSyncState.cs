namespace Meshline.Models.Client;

/// <summary>
/// Describes the progress of resource synchronization.
/// </summary>
public enum ResourceSyncState
{
    /// <summary>
    /// No synchronization pass is currently active.
    /// </summary>
    Idle,
    /// <summary>
    /// A synchronization pass is running.
    /// </summary>
    Synchronizing,
    /// <summary>
    /// The last synchronization pass reached the observed timeline head.
    /// </summary>
    CaughtUp,
    /// <summary>
    /// Synchronization cannot advance until its blocking condition is resolved.
    /// </summary>
    Blocked
}
