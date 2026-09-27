namespace Meshline.Components;

/// <summary>
/// Identifies how a channel post changed.
/// </summary>
public enum ChannelPostChangeKind
{
    /// <summary>
    /// A post was added to the local timeline.
    /// </summary>
    Added,
    /// <summary>
    /// The locally available post content changed.
    /// </summary>
    Edited,
    /// <summary>
    /// The post was deleted from the materialized timeline.
    /// </summary>
    Deleted
}
