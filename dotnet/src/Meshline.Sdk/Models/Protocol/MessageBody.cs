using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a text body and its protocol-supported media type.
/// </summary>
public sealed record MessageBody : ProtocolModel
{
    /// <summary>
    /// The media type describing the content.
    /// </summary>
    public required string ContentType { get; init; }
    /// <summary>
    /// The text content of the message body.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Validates the text media type, character set, and body text constraints.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (!MediaTypeValidator.IsValid(ContentType, requireUtf8Charset: true))
            return new(ProtocolViolationKind.Format, "The body must have a valid media type with an omitted or UTF-8 charset.");
        return string.IsNullOrWhiteSpace(Text)
            ? new(ProtocolViolationKind.Format, "The message body must contain non-whitespace text.")
            : null;
    }
}
