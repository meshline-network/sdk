using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Combines group state with the local account's membership and role.
/// </summary>
public sealed class GroupInfo
{
    /// <summary>
    /// The group and hosting relay to which this information applies.
    /// </summary>
    public required GroupRef Ref { get; init; }
    /// <summary>
    /// The currently known relay-visible group state.
    /// </summary>
    public required GroupState Group { get; init; }
    /// <summary>
    /// The local account's membership state in the group.
    /// </summary>
    public required GroupMembershipState Membership { get; init; }
    /// <summary>
    /// The local account's role, or <see langword="null"/> when no member role is known.
    /// </summary>
    public GroupRole? Role { get; init; }
}
