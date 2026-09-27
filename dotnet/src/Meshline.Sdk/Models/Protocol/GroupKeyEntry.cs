using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains encrypted client and relay secrets for one group epoch.
/// </summary>
public sealed record GroupKeyEntry : ProtocolModel
{
    /// <summary>
    /// The group key epoch associated with this record.
    /// </summary>
    public required long Epoch { get; init; }
    /// <summary>
    /// The encrypted client secret for this epoch when it changes; later entries may omit it to reuse the preceding client secret.
    /// </summary>
    public GroupSecretBox? ClientSecretBox { get; init; }
    /// <summary>
    /// The relay contribution to the group secret, encrypted for the member.
    /// </summary>
    public required GroupSecretBox RelaySecretBox { get; init; }

    /// <summary>
    /// Validates the epoch and encrypted client and relay secret boxes.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Epoch < 0)
            return new(ProtocolViolationKind.Format, "The group key epoch must be nonnegative.");
        return ClientSecretBox?.Validate() ?? RelaySecretBox.Validate();
    }
}
