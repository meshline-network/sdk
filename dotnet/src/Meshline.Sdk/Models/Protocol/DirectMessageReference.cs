using Meshline.Identity;
using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Identifies a direct message by its sender and message identifier.
/// </summary>
public sealed record DirectMessageReference : ProtocolModel
{
    /// <summary>
    /// The CAIP-10 account identifier of the message sender.
    /// </summary>
    public required string From { get; init; }
    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// Validates the referenced sender account and message identifier.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateMessageId(MessageId) is { } messageViolation)
            return messageViolation;
        try
        {
            return AccountAdapter.ValidateAccountId(From);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
