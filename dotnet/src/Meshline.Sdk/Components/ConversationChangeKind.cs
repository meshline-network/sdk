namespace Meshline.Components;

/// <summary>
/// Identifies whether a local conversation was created, updated, or removed.
/// </summary>
public enum ConversationChangeKind
{
    /// <summary>
    /// A conversation became available locally.
    /// </summary>
    Created,
    /// <summary>
    /// A conversation's latest message or unread count changed.
    /// </summary>
    Updated,
    /// <summary>
    /// A conversation is no longer included in the local conversation list.
    /// </summary>
    Removed
}
