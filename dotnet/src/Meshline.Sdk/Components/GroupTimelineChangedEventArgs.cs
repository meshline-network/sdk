using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Contains group messages newly made available in the local timeline.
/// </summary>
public sealed class GroupTimelineChangedEventArgs : EventArgs
{
    /// <summary>
    /// The messages made available by this local timeline update.
    /// </summary>
    /// <remarks>Includes synchronized, decrypted and restored history. Read status follows the target conversation's read position.</remarks>
    public IReadOnlyList<GroupMessageInfo> Messages { get; }

    internal GroupTimelineChangedEventArgs(IReadOnlyList<GroupMessageInfo> messages)
    {
        Messages = Array.AsReadOnly(messages.ToArray());
    }
}
