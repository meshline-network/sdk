using Meshline.Validation;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Changes group metadata through a signed management-chain operation.
/// </summary>
public sealed record GroupUpdate() : GroupManagementOperation("meshline.group.update")
{
    /// <summary>
    /// The update to the display name; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<string> Name { get; init; }
    /// <summary>
    /// The update to the description; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<string> Description { get; init; }
    /// <summary>
    /// The update to the group invitation policy; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<GroupInvitePolicy> InvitePolicy { get; init; }
    /// <summary>
    /// The update to the group member capacity; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<long> MemberCapacity { get; init; }

    /// <summary>
    /// Validates management-chain fields and changed metadata, rejecting deletion of required group fields.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (base.Validate(context) is { } violation)
            return violation;
        if (Name.IsDeleted || InvitePolicy.IsDeleted || MemberCapacity.IsDeleted)
            return new(ProtocolViolationKind.Format, "The group name, member capacity, and invitation policy cannot be deleted.");
        if (!Name.IsSpecified && !Description.IsSpecified && !Description.IsDeleted && !InvitePolicy.IsSpecified && !MemberCapacity.IsSpecified)
            return new(ProtocolViolationKind.Format, "A group update must specify at least one modifiable field.");
        if (Name.IsSpecified && (string.IsNullOrWhiteSpace(Name.Value) || Encoding.UTF8.GetByteCount(Name.Value) > 256))
            return new(ProtocolViolationKind.Format, "The group name must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.");
        if (Description.IsSpecified && !Description.IsDeleted && Description.Value is { Length: > 0 } description && (string.IsNullOrWhiteSpace(description) || Encoding.UTF8.GetByteCount(description) > 4096))
            return new(ProtocolViolationKind.Format, "A nonempty group description must contain non-whitespace text and cannot exceed 4096 UTF-8 bytes.");
        return MemberCapacity.IsSpecified && MemberCapacity.Value <= 0
            ? new(ProtocolViolationKind.Format, "The group member capacity must be positive.")
            : null;
    }
}
