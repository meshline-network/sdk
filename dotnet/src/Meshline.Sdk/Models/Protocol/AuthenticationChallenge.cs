using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a relay-issued authentication nonce and its validity window.
/// </summary>
public sealed record AuthenticationChallenge : ProtocolModel
{
    /// <summary>
    /// The relay-issued authentication challenge string.
    /// </summary>
    public required string Nonce { get; init; }
    /// <summary>
    /// The creation time, in Unix seconds.
    /// </summary>
    public required long CreatedAt { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }

    /// <summary>
    /// Validates the authentication nonce and its bounded creation and expiration window.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Nonce.Length is 0 or > 256 || Nonce.Any(static character => character is < '!' or > '~'))
            return new(ProtocolViolationKind.Format, "The challenge nonce must contain 1 to 256 visible ASCII characters.");
        return CreatedAt < 0 || ExpiresAt <= CreatedAt
            ? new(ProtocolViolationKind.Time, "The challenge expiry must follow its nonnegative creation time.")
            : null;
    }
}
