namespace Meshline.Models.Protocol;

/// <summary>
/// Selects the relay delivery status of a message by identifier.
/// </summary>
public sealed record MessageDeliveryQuery : ProtocolModel
{
    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public required string MessageId { get; init; }
}
