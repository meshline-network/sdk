using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Supplies the name, capacity, and invitation policy for a new encrypted group.
/// </summary>
public sealed class GroupCreateOptions
{
    /// <summary>
    /// The display name.
    /// </summary>
    public required string Name { get; set; }
    /// <summary>
    /// The optional human-readable description.
    /// </summary>
    public string? Description { get; set; }
    /// <summary>
    /// The maximum permitted number of group members.
    /// </summary>
    public required long MemberCapacity { get; set; }
    /// <summary>
    /// The policy controlling which members can create invitations.
    /// </summary>
    public GroupInvitePolicy InvitePolicy { get; set; } = GroupInvitePolicy.Administrators;
}
