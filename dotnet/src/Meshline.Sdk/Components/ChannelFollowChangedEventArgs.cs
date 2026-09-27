namespace Meshline.Components;

/// <summary>
/// Describes a change to the local following state of a channel.
/// </summary>
public sealed class ChannelFollowChangedEventArgs : EventArgs
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public string ChannelId { get; }
    /// <summary>
    /// Whether this device locally follows the channel.
    /// </summary>
    public bool IsFollowed { get; }

    internal ChannelFollowChangedEventArgs(string channelId, bool isFollowed)
    {
        ChannelId = channelId;
        IsFollowed = isFollowed;
    }
}
