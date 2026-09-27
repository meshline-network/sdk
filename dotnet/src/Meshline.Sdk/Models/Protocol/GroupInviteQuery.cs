using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Selects an invitation within a group.
/// </summary>
public sealed record GroupInviteQuery : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The invitation's canonical <c>inv_</c> identifier.
    /// </summary>
    public required string InviteId { get; init; }

    /// <summary>
    /// Validates the group and invitation identifiers.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        Identifiers.ValidateGroupId(GroupId) ?? Identifiers.ValidateInviteId(InviteId);
}
