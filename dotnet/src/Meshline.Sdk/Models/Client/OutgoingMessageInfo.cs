using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Contains the plaintext content and send status of a retained outgoing direct message from one local snapshot.
/// </summary>
/// <remarks>
/// Outbox entries have no local history sequence. Match <see cref="Status"/>'s message identifier with the current account's history to avoid displaying a synchronized send twice.
/// </remarks>
public sealed class OutgoingMessageInfo
{
    /// <summary>
    /// The plaintext body, attachment references, and optional reply reference.
    /// </summary>
    public required DirectMessage Message { get; init; }

    /// <summary>
    /// The message identifier, recipient, creation time, and submission or delivery state captured with the content.
    /// </summary>
    public required MessageSendStatus Status { get; init; }
}
