namespace Meshline.Models.Client;

/// <summary>
/// Filters local conversations by kind, unread state, and message presence.
/// </summary>
public sealed class ConversationQuery
{
    /// <summary>
    /// The conversation kinds to include; defaults to all supported kinds.
    /// </summary>
    public ConversationKind Kind { get; init; } = ConversationKind.All;
    /// <summary>
    /// Whether to return only conversations with unread messages.
    /// </summary>
    public bool UnreadOnly { get; init; }
    /// <summary>
    /// A message-presence filter: <see langword="true"/> for nonempty, <see langword="false"/> for empty, or <see langword="null"/> for both.
    /// </summary>
    public bool? HasMessages { get; init; }
}
