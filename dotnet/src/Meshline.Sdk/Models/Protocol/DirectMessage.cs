using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains the plaintext body, attachments, and reply reference encrypted in a direct message.
/// </summary>
public sealed record DirectMessage() : TypedProtocolModel("meshline.message.direct")
{
    /// <summary>
    /// The optional plaintext message body.
    /// </summary>
    public MessageBody? Body { get; init; }
    /// <summary>
    /// The content references attached to the message.
    /// </summary>
    public ImmutableArray<ContentReference>? Attachments { get; init; }
    /// <summary>
    /// The referenced direct message being replied to, or <see langword="null"/> for a new message.
    /// </summary>
    public DirectMessageReference? ReplyTo { get; init; }

    /// <summary>
    /// Validates plaintext content, attachment references, and the optional direct-message reply identifier.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        MessageContentValidator.Validate(Body, Attachments) ?? ReplyTo?.Validate(context);
}
