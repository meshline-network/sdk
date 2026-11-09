namespace Meshline.Components;

/// <summary>
/// Describes the local following state made available by a channel update.
/// </summary>
/// <remarks>A restored channel entry reports its initial state, including an unfollowed state.</remarks>
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
