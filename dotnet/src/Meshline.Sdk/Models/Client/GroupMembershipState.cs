namespace Meshline.Models.Client;

/// <summary>
/// Describes the local account's membership or admission state in a group.
/// </summary>
public enum GroupMembershipState
{
    /// <summary>
    /// Membership has not yet been determined.
    /// </summary>
    Unknown,
    /// <summary>
    /// The local account is not a group member.
    /// </summary>
    NotMember,
    /// <summary>
    /// The local account has a pending admission application.
    /// </summary>
    Pending,
    /// <summary>
    /// The local account is a group member.
    /// </summary>
    Member,
    /// <summary>
    /// The local account left the group.
    /// </summary>
    Left,
    /// <summary>
    /// The local account was removed from the group.
    /// </summary>
    Removed,
    /// <summary>
    /// The local account is banned from the group.
    /// </summary>
    Banned
}
