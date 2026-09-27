namespace Meshline.Models.Protocol;

/// <summary>
/// Indicates whether a channel remains active or has been closed.
/// </summary>
public enum ChannelStatus
{
    /// <summary>
    /// The channel is active.
    /// </summary>
    Active,
    /// <summary>
    /// The channel has been closed.
    /// </summary>
    Closed,
}
