namespace Meshline.Components;

/// <summary>
/// Identifies a conversation whose locally computed summary changed.
/// </summary>
public sealed class ConversationChangedEventArgs : EventArgs
{
    /// <summary>
    /// The peer account identifier for a direct conversation, or the group or channel identifier.
    /// </summary>
    public string ConversationId { get; }
    /// <summary>
    /// The kind or combination of changes reported by this event.
    /// </summary>
    public ConversationChangeKind ChangeKind { get; }

    internal ConversationChangedEventArgs(string conversationId, ConversationChangeKind changeKind)
    {
        ConversationId = conversationId;
        ChangeKind = changeKind;
    }
}
