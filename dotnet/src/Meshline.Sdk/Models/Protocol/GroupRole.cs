namespace Meshline.Models.Protocol;

/// <summary>
/// Identifies a member's authority within a group.
/// </summary>
public enum GroupRole
{
    /// <summary>
    /// The member owns the group.
    /// </summary>
    Owner,
    /// <summary>
    /// The member has group administration privileges.
    /// </summary>
    Administrator,
    /// <summary>
    /// The member has ordinary group membership privileges.
    /// </summary>
    Member
}
