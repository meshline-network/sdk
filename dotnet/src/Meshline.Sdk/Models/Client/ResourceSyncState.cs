namespace Meshline.Models.Client;

/// <summary>
/// Describes the progress of resource synchronization.
/// </summary>
public enum ResourceSyncState
{
    /// <summary>
    /// Synchronization has not started, was canceled, or has stopped.
    /// </summary>
    Idle,
    /// <summary>
    /// A synchronization pass is running.
    /// </summary>
    Synchronizing,
    /// <summary>
    /// The last complete pass reached the observed timeline head and processed readable messages, including required group decryption. This does not guarantee complete retained history.
    /// </summary>
    CaughtUp,
    /// <summary>
    /// Synchronization cannot complete until its blocking condition is resolved. Timeline ingestion may continue while group keys are unavailable.
    /// </summary>
    Blocked
}
