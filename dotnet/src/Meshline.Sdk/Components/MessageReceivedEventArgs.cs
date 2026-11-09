using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Contains direct messages made available in local storage by synchronization or backup import.
/// </summary>
public sealed class MessageReceivedEventArgs : EventArgs
{
    /// <summary>
    /// The messages made available by this local update.
    /// </summary>
    /// <remarks>Includes synchronized and restored history. Read status follows the target conversation's read position.</remarks>
    public IReadOnlyList<MessageInfo> Messages { get; }

    internal MessageReceivedEventArgs(IReadOnlyList<MessageInfo> messages)
    {
        Messages = Array.AsReadOnly(messages.ToArray());
    }
}
