using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Supplies plaintext content and an optional reply sequence for an encrypted group message.
/// </summary>
public sealed class GroupMessageDraft
{
    /// <summary>
    /// The optional plaintext message body.
    /// </summary>
    public MessageBody? Body { get; set; }
    /// <summary>
    /// The content references attached to the message.
    /// </summary>
    public List<ContentReference> Attachments { get; set; } = [];
    /// <summary>
    /// The group message sequence being replied to, or <see langword="null"/> for a new message.
    /// </summary>
    public long? ReplyToSequence { get; set; }
}
