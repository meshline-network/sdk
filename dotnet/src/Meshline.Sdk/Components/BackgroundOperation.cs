namespace Meshline.Components;

/// <summary>
/// Identifies the background activity that produced a diagnostic error.
/// </summary>
public enum BackgroundOperation
{
    /// <summary>
    /// Relay connection or authentication work.
    /// </summary>
    Connect,
    /// <summary>
    /// Account, channel, or group synchronization work.
    /// </summary>
    Synchronize,
    /// <summary>
    /// Queued outgoing-message processing.
    /// </summary>
    SendMessage
}
