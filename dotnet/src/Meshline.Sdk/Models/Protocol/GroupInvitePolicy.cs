namespace Meshline.Models.Protocol;

/// <summary>
/// Determines which members may create targeted or shareable group invitations.
/// </summary>
public enum GroupInvitePolicy
{
    /// <summary>
    /// Only the owner and administrators may create invitations.
    /// </summary>
    Administrators,
    /// <summary>
    /// Ordinary members may create invitations restricted to a named account.
    /// </summary>
    MembersTargeted,
    /// <summary>
    /// Ordinary members may create shareable invitations as well as targeted invitations.
    /// </summary>
    MembersShareable
}
