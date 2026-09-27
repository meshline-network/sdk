using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Combines a group creation document with the owner's encrypted client-secret box.
/// </summary>
public sealed record GroupCreateRequest : ProtocolModel
{
    /// <summary>
    /// The signed group creation document.
    /// </summary>
    public required GroupCreate Create { get; init; }
    /// <summary>
    /// The client contribution to the group secret, encrypted for the member.
    /// </summary>
    public required GroupSecretBox ClientSecretBox { get; init; }

    /// <summary>
    /// Validates the group creation document and the owner's encrypted client-secret box.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        Create.Validate() ?? ClientSecretBox.Validate();
}
