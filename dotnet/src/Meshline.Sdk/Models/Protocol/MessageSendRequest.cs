using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Combines an encrypted message envelope with device key boxes and optional contact authorization.
/// </summary>
public sealed record MessageSendRequest : ProtocolModel
{
    /// <summary>
    /// The signed encrypted message envelope.
    /// </summary>
    public required MessageEnvelope Envelope { get; init; }
    /// <summary>
    /// Optional message key boxes for other devices of the sender's account.
    /// </summary>
    public ImmutableArray<MessageKeyBox>? SenderBoxes { get; init; }
    /// <summary>
    /// Message key boxes for the recipient's authorized devices.
    /// </summary>
    public required ImmutableArray<MessageKeyBox> RecipientBoxes { get; init; }
    /// <summary>
    /// The contact grant or invitation supplied as authorization evidence.
    /// </summary>
    public TypedProtocolModel? Authorization { get; init; }
}
