namespace Meshline.Models.Client;

/// <summary>
/// Contains a locally stored contact request and any associated outbox status.
/// </summary>
public sealed class ContactRequestInfo
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string AccountId { get; init; }
    /// <summary>
    /// Whether the contact request is incoming or outgoing.
    /// </summary>
    public required ContactRequestDirection Direction { get; init; }
    /// <summary>
    /// The optional note accompanying the contact request.
    /// </summary>
    public string? Note { get; init; }
    /// <summary>
    /// The time the contact request was created locally.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public string? MessageId { get; init; }
    /// <summary>
    /// The associated outbox state, or <see langword="null"/> when there is no outgoing operation.
    /// </summary>
    public MessageSendState? SendState { get; init; }
}
