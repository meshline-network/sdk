using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Requests private group membership state from another device of the same account.
/// </summary>
public sealed record AccountGroupPrivateStateRequest() : TypedProtocolModel("meshline.account.group.state.request")
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier, or <see langword="null"/> to request all available private group states.
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// Validates the optional group identifier in a private-state request.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        GroupId is null ? null : Identifiers.ValidateGroupId(GroupId);
}
