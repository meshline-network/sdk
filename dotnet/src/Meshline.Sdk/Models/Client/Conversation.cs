namespace Meshline.Models.Client;

/// <summary>
/// Summarizes a local direct, group, or channel conversation.
/// </summary>
public sealed class Conversation
{
    /// <summary>
    /// The peer account identifier for a direct conversation, or the group or channel identifier.
    /// </summary>
    public required string ConversationId { get; init; }
    /// <summary>
    /// The kind of conversation represented by this summary.
    /// </summary>
    public required ConversationKind Kind { get; init; }
    /// <summary>
    /// The latest locally available message preview, or <see langword="null"/> when no message is available.
    /// </summary>
    public ConversationSummary? Latest { get; init; }
    /// <summary>
    /// The number of locally available unread messages according to this device's read position.
    /// </summary>
    public long UnreadCount { get; init; }
}
