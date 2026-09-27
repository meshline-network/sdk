namespace Meshline.Models.Client;

/// <summary>
/// Contains the latest message preview used to summarize a conversation.
/// </summary>
public sealed class ConversationSummary
{
    /// <summary>
    /// The CAIP-10 account identifier of the message sender.
    /// </summary>
    public required string Sender { get; init; }
    /// <summary>
    /// The message or request timestamp.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>
    /// The text content of the message body.
    /// </summary>
    public string? Text { get; init; }
    /// <summary>
    /// Whether the summarized message contains attachments.
    /// </summary>
    public bool HasAttachments { get; init; }
}
