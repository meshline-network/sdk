using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Contains updated group information and the aspects that changed.
/// </summary>
public sealed class GroupChangedEventArgs : EventArgs
{
    /// <summary>
    /// The updated group snapshot.
    /// </summary>
    public GroupInfo Group { get; }
    /// <summary>
    /// The kind or combination of changes reported by this event.
    /// </summary>
    public GroupChangeKind ChangeKind { get; }

    internal GroupChangedEventArgs(GroupInfo group, GroupChangeKind changeKind)
    {
        Group = group;
        ChangeKind = changeKind;
    }
}
