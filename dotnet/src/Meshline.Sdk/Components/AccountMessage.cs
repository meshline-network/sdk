using Meshline.Models.Protocol;

namespace Meshline.Components;

sealed class AccountMessage
{
    public required long LocalSequence { get; init; }
    public required string Sender { get; init; }
    public required string SenderDeviceId { get; init; }
    public required string Recipient { get; init; }
    public required string MessageId { get; init; }
    public required TypedProtocolModel Payload { get; init; }
}
