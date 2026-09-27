using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Supplies the body and attachments for a new public channel post.
/// </summary>
public sealed class ChannelPostDraft
{
    /// <summary>
    /// The optional plaintext message body.
    /// </summary>
    public MessageBody? Body { get; set; }
    /// <summary>
    /// The content references attached to the message.
    /// </summary>
    public List<ContentReference> Attachments { get; set; } = [];
}
