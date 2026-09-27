namespace Meshline.Components;

/// <summary>
/// Describes the initialization and runtime lifecycle of a client component.
/// </summary>
public enum ComponentState
{
    /// <summary>
    /// Initialization has not completed.
    /// </summary>
    Uninitialized,
    /// <summary>
    /// Initialization completed and background work is stopped.
    /// </summary>
    Stopped,
    /// <summary>
    /// Background work is running.
    /// </summary>
    Running,
    /// <summary>
    /// Background work is being stopped.
    /// </summary>
    Stopping,
    /// <summary>
    /// Resources were released and the component can no longer be used.
    /// </summary>
    Disposed
}
