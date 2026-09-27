using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Identifies a group whose pending admission applications changed.
/// </summary>
public sealed class GroupApplicationsChangedEventArgs : EventArgs
{
    /// <summary>
    /// The group and hosting relay associated with this information.
    /// </summary>
    public GroupRef Group { get; }

    internal GroupApplicationsChangedEventArgs(GroupRef group)
    {
        Group = group;
    }
}
