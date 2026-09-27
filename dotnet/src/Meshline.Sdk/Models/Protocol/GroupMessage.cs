using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains plaintext message content encrypted within a group message envelope.
/// </summary>
public sealed record GroupMessage() : TypedProtocolModel("meshline.group.message.content")
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
    /// The group message sequence being replied to, or <see langword="null"/> for a new message.
    /// </summary>
    public long? ReplyToSeq { get; init; }

    /// <summary>
    /// Validates plaintext group content, attachments, and the optional reply sequence.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (ReplyToSeq is <= 0)
            return new(ProtocolViolationKind.Format, "The replied-to group message sequence must be positive.");
        return MessageContentValidator.Validate(Body, Attachments);
    }
}
