using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Contains group messages newly made available in the local timeline.
/// </summary>
public sealed class GroupTimelineChangedEventArgs : EventArgs
{
    /// <summary>
    /// The verified messages made available by this timeline update.
    /// </summary>
    public IReadOnlyList<GroupMessageInfo> Messages { get; }

    internal GroupTimelineChangedEventArgs(IReadOnlyList<GroupMessageInfo> messages)
    {
        Messages = Array.AsReadOnly(messages.ToArray());
    }
}
