using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Identifies a group whose pending member key recovery requests changed.
/// </summary>
public sealed class GroupKeyRecoveryChangedEventArgs : EventArgs
{
    /// <summary>
    /// The group and hosting relay associated with this information.
    /// </summary>
    public GroupRef Group { get; }

    internal GroupKeyRecoveryChangedEventArgs(GroupRef group)
    {
        Group = group;
    }
}
