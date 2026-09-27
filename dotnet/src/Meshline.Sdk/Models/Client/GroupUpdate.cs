using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Describes metadata field updates for a group.
/// </summary>
public sealed class GroupUpdate
{
    /// <summary>
    /// The update to the display name; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<string> Name { get; set; }
    /// <summary>
    /// The update to the description; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<string> Description { get; set; }
    /// <summary>
    /// The update to the group member capacity; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<long> MemberCapacity { get; set; }
    /// <summary>
    /// The update to the group invitation policy; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<GroupInvitePolicy> InvitePolicy { get; set; }
}
