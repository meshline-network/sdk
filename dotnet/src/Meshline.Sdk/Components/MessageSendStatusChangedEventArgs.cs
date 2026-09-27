using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Contains the updated state of a direct-message outbox operation.
/// </summary>
public sealed class MessageSendStatusChangedEventArgs : EventArgs
{
    /// <summary>
    /// The updated direct-message outbox status.
    /// </summary>
    public MessageSendStatus Status { get; }

    internal MessageSendStatusChangedEventArgs(MessageSendStatus status)
    {
        Status = status;
    }
}
