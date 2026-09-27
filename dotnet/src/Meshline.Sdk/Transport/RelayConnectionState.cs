namespace Meshline.Transport;

/// <summary>
/// Describes the relay client's observed communication state.
/// </summary>
public enum RelayConnectionState
{
    /// <summary>
    /// No current successful connection is recorded.
    /// </summary>
    Disconnected,
    /// <summary>
    /// A connection attempt is in progress.
    /// </summary>
    Connecting,
    /// <summary>
    /// Communication with the relay has succeeded or a socket is connected.
    /// </summary>
    Connected
}
