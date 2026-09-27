using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Describes body and attachment field updates for a public channel post.
/// </summary>
public sealed class ChannelPostUpdate
{
    /// <summary>
    /// The update to the message body; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<MessageBody> Body { get; set; }
    /// <summary>
    /// The update to the attachment list; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<IReadOnlyList<ContentReference>> Attachments { get; set; }
}
