namespace Meshline.Models.Client;

/// <summary>
/// Selects the kinds of local conversations to include; values may be combined.
/// </summary>
[Flags]
public enum ConversationKind
{
    /// <summary>
    /// No conversation kinds are selected.
    /// </summary>
    None = 0,
    /// <summary>
    /// Direct-message conversations with other accounts.
    /// </summary>
    Direct = 1,
    /// <summary>
    /// Encrypted group conversations.
    /// </summary>
    Group = 2,
    /// <summary>
    /// Followed public channel conversations.
    /// </summary>
    Channel = 4,
    /// <summary>
    /// All supported conversation kinds.
    /// </summary>
    All = Direct | Group | Channel
}
