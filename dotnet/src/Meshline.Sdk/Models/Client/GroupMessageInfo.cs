using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Contains a verified and decrypted group message from the locally stored timeline.
/// </summary>
public sealed class GroupMessageInfo
{
    /// <summary>
    /// The group and hosting relay associated with this information.
    /// </summary>
    public required GroupRef Group { get; init; }
    /// <summary>
    /// The timeline sequence assigned by the hosting relay.
    /// </summary>
    public required long Sequence { get; init; }
    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public required string MessageId { get; init; }
    /// <summary>
    /// The CAIP-10 account identifier of the message sender.
    /// </summary>
    public required string Sender { get; init; }
    /// <summary>
    /// The identifier of the device that signed the message.
    /// </summary>
    public required string SenderDeviceId { get; init; }
    /// <summary>
    /// The creation time.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>
    /// The optional plaintext message body.
    /// </summary>
    public MessageBody? Body { get; init; }
    /// <summary>
    /// The content references attached to the message.
    /// </summary>
    public IReadOnlyList<ContentReference>? Attachments { get; init => field = value?.ToArray().AsReadOnly(); }
    /// <summary>
    /// The group message sequence being replied to, or <see langword="null"/> for a new message.
    /// </summary>
    public long? ReplyToSequence { get; init; }
}
