using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Contains the post changes observed while updating a channel timeline.
/// </summary>
public sealed class ChannelTimelineChangedEventArgs : EventArgs
{
    /// <summary>
    /// The channel whose locally stored timeline changed.
    /// </summary>
    public ChannelRef Ref { get; }
    /// <summary>
    /// The channel post changes committed during this timeline update.
    /// </summary>
    public IReadOnlyList<ChannelPostChange> Changes { get; }

    internal ChannelTimelineChangedEventArgs(ChannelRef channel, IReadOnlyList<ChannelPostChange> changes)
    {
        Ref = channel;
        Changes = Array.AsReadOnly(changes.ToArray());
    }
}
