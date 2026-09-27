using Meshline.Validation;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a relay session bearer token, authentication mode, and expiration time.
/// </summary>
public sealed record SessionCredentials : ProtocolModel
{
    /// <summary>
    /// The session bearer token; treat it as a credential and avoid logging it.
    /// </summary>
    public required string Token { get; init; }
    /// <summary>
    /// The authorization mode to which the session is bound.
    /// </summary>
    public required SessionMode Mode { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }

    /// <summary>
    /// Validates the bearer token format, session mode, and expiry.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Token.Length is 0 or > 256 || Token.Any(static character => character is < '!' or > '~'))
            return new(ProtocolViolationKind.Format, "The session token must contain 1 to 256 visible ASCII characters.");
        if (!Enum.IsDefined(Mode))
            return new(ProtocolViolationKind.Format, "Unknown session mode.");
        return ExpiresAt <= 0
            ? new(ProtocolViolationKind.Time, "The session expiry must be positive.")
            : null;
    }

    /// <summary>
    /// Appends the record's diagnostic fields while omitting sensitive material from its string representation.
    /// </summary>
    /// <param name="builder">The builder to which diagnostic fields are appended.</param>
    /// <returns><see langword="true"/> after appending the record's diagnostic fields.</returns>
    protected override bool PrintMembers(StringBuilder builder)
    {
        if (base.PrintMembers(builder))
            builder.Append(", ");

        builder.Append($"{nameof(Mode)} = {Mode}, {nameof(ExpiresAt)} = {ExpiresAt}");
        return true;
    }
}
