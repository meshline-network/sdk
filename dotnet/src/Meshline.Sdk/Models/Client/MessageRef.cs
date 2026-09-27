using Meshline.Identity;
using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Identifies a direct message by the sender account and message identifier.
/// </summary>
public sealed record MessageRef
{
    /// <summary>
    /// The CAIP-10 account identifier of the message sender.
    /// </summary>
    public required string Sender
    {
        get;
        init
        {
            if (AccountAdapter.ValidateAccountId(value) is { } violation)
                throw new ArgumentException(violation.Message, nameof(Sender));
            field = value;
        }
    }

    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public required string MessageId
    {
        get;
        init
        {
            if (Identifiers.ValidateMessageId(value) is { } violation)
                throw new ArgumentException(violation.Message, nameof(MessageId));
            field = value;
        }
    }
}
