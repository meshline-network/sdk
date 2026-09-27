using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Contains direct messages received and committed to local storage.
/// </summary>
public sealed class MessageReceivedEventArgs : EventArgs
{
    /// <summary>
    /// The verified messages made available by this timeline update.
    /// </summary>
    public IReadOnlyList<MessageInfo> Messages { get; }

    internal MessageReceivedEventArgs(IReadOnlyList<MessageInfo> messages)
    {
        Messages = Array.AsReadOnly(messages.ToArray());
    }
}
