using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Supplies plaintext content and an optional reply reference for an encrypted direct message.
/// </summary>
public sealed class DirectMessageDraft
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
    /// The referenced direct message being replied to, or <see langword="null"/> for a new message.
    /// </summary>
    public MessageRef? ReplyTo { get; set; }
}
