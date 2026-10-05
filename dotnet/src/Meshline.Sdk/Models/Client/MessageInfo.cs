using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Contains a verified and decrypted direct message retained in local storage.
/// </summary>
public sealed class MessageInfo
{
    /// <summary>
    /// The monotonically assigned sequence in this local database's account-message stream. Values may have gaps and are not comparable across databases or with relay timeline sequences.
    /// </summary>
    public required long LocalSequence { get; init; }
    /// <summary>
    /// The message reference consisting of the sender account and message identifier.
    /// </summary>
    public required MessageRef Key { get; init; }
    /// <summary>
    /// The identifier of the device that signed the message.
    /// </summary>
    public required string SenderDeviceId { get; init; }
    /// <summary>
    /// The CAIP-10 account identifier of the message recipient.
    /// </summary>
    public required string Recipient { get; init; }
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
    /// The referenced direct message being replied to, or <see langword="null"/> for a new message.
    /// </summary>
    public MessageRef? ReplyTo { get; init; }
}
