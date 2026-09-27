namespace Meshline.Models.Client;

/// <summary>
/// Contains the locally tracked submission and delivery state of an outgoing direct message.
/// </summary>
public sealed class MessageSendStatus
{
    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public required string MessageId { get; init; }
    /// <summary>
    /// The CAIP-10 account identifier of the message recipient.
    /// </summary>
    public required string Recipient { get; init; }
    /// <summary>
    /// The local outbox operation's current submission or delivery state.
    /// </summary>
    public MessageSendState State { get; init; }
    /// <summary>
    /// The creation time.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>
    /// The relay that accepted the outgoing message, or <see langword="null"/> before acceptance is known.
    /// </summary>
    public string? AcceptedRelayId { get; init; }
    /// <summary>
    /// The relay acceptance time, or <see langword="null"/> when unavailable.
    /// </summary>
    public DateTimeOffset? AcceptedAt { get; init; }
    /// <summary>
    /// The latest outgoing-message failure description, when available.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
