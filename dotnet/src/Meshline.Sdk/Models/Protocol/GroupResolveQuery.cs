using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Selects group state, optionally using an invitation as access evidence.
/// </summary>
public sealed record GroupResolveQuery : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The invitation's canonical <c>inv_</c> identifier.
    /// </summary>
    public string? InviteId { get; init; }

    /// <summary>
    /// Validates the group identifier and optional invitation identifier.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        Identifiers.ValidateGroupId(GroupId) ?? (InviteId is null ? null : Identifiers.ValidateInviteId(InviteId));
}
