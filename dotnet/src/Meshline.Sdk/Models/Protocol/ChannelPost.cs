using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains the device-signed content of a public channel post.
/// </summary>
public sealed record ChannelPost() : TypedProtocolModel("meshline.channel.post")
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId { get; init; }
    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public required string MessageId { get; init; }
    /// <summary>
    /// The optional plaintext message body.
    /// </summary>
    public MessageBody? Body { get; init; }
    /// <summary>
    /// The content references attached to the message.
    /// </summary>
    public ImmutableArray<ContentReference>? Attachments { get; init; }
    /// <summary>
    /// The device signature over the model's device signing input.
    /// </summary>
    public required ImmutableArray<byte> DeviceSignature { get; init; }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(NetworkContext context) =>
        GetSigningInput(context, "device_signature");

    /// <summary>
    /// Validates the post identifiers, body, attachments, and signature length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateChannelId(ChannelId) is { } channelViolation)
            return channelViolation;
        if (Identifiers.ValidateMessageId(MessageId) is { } messageViolation)
            return messageViolation;
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The channel post signature must contain 64 bytes.");
        if (MessageContentValidator.Validate(Body, Attachments) is { } contentViolation)
            return contentViolation;
        try
        {
            return Encoding.UTF8.GetByteCount(ToJson()) <= 65536
                ? null
                : new(ProtocolViolationKind.Format, "The channel post cannot exceed 65536 canonical JSON bytes.");
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
    }
}
