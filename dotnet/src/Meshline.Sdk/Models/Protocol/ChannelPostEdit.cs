using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Carries signed body and attachment updates for an existing channel post.
/// </summary>
public sealed record ChannelPostEdit() : TypedProtocolModel("meshline.channel.post.edit")
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId { get; init; }
    /// <summary>
    /// The original timeline sequence of the channel post being modified or reported.
    /// </summary>
    public required long TargetSequence { get; init; }
    /// <summary>
    /// The update to the message body; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<MessageBody> Body { get; init; }
    /// <summary>
    /// The update to the attachment list; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<ImmutableArray<ContentReference>> Attachments { get; init; }
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
    /// Validates the target post, field updates, and signature length.
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
        if (TargetSequence <= 0)
            return new(ProtocolViolationKind.Format, "The target post sequence must be positive.");
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The channel post edit signature must contain 64 bytes.");
        if (!Body.IsSpecified && !Body.IsDeleted && !Attachments.IsSpecified && !Attachments.IsDeleted && AdditionalProperties.IsEmpty)
            return new(ProtocolViolationKind.Format, "A channel post edit must change at least one field.");
        foreach (var name in AdditionalProperties.Keys)
        {
            if (name is "$type" or "$context" or "channel_id" or "message_id" or "body" or "attachments" or "device_signature")
                return new(ProtocolViolationKind.Format, "Edit extensions cannot replace defined channel post fields or the network context.");
        }
        if (Body.IsSpecified && !Body.IsDeleted && Body.Value.Validate(context) is { } bodyViolation)
            return bodyViolation;
        if (Attachments.IsSpecified && !Attachments.IsDeleted && MessageContentValidator.ValidateAttachments(Attachments.Value) is { } attachmentsViolation)
            return attachmentsViolation;
        if (Body.IsDeleted && (Attachments.IsDeleted || Attachments.IsSpecified && Attachments.Value.IsEmpty))
            return new(ProtocolViolationKind.Format, "An edit cannot remove both the body and every attachment.");
        try
        {
            return Encoding.UTF8.GetByteCount(ToJson()) <= 65536
                ? null
                : new(ProtocolViolationKind.Format, "The channel post edit cannot exceed 65536 canonical JSON bytes.");
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
    }
}
