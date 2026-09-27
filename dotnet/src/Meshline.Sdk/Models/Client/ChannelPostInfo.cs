using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Contains the current locally materialized content and identity of a channel post.
/// </summary>
public sealed class ChannelPostInfo
{
    /// <summary>
    /// The channel and original timeline sequence identifying this post.
    /// </summary>
    public required ChannelPostRef Ref { get; init; }
    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public required string MessageId { get; init; }
    /// <summary>
    /// The CAIP-10 account identifier of the post's author.
    /// </summary>
    public required string Author { get; init; }
    /// <summary>
    /// The relay acceptance time, or <see langword="null"/> when unavailable.
    /// </summary>
    public DateTimeOffset? AcceptedAt { get; init; }
    /// <summary>
    /// The optional plaintext message body.
    /// </summary>
    public MessageBody? Body { get; init; }
    /// <summary>
    /// The content references attached to the message.
    /// </summary>
    public IReadOnlyList<ContentReference>? Attachments { get; init => field = value?.ToArray().AsReadOnly(); }
}
