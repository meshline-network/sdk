using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a signed encrypted group message bound to one group and key epoch.
/// </summary>
public sealed record GroupMessageEnvelope() : TypedProtocolModel("meshline.group.message")
{
    /// <summary>
    /// The message's canonical <c>msg_</c> identifier.
    /// </summary>
    public required string MessageId { get; init; }
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The group key epoch associated with this record.
    /// </summary>
    public required long Epoch { get; init; }
    /// <summary>
    /// The creation time, in Unix seconds.
    /// </summary>
    public required long CreatedAt { get; init; }
    /// <summary>
    /// The encrypted group-message content.
    /// </summary>
    public required EncryptedPayload Payload { get; init; }
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
    /// Validates group message identifiers, epoch, time, encrypted payload, size, and signature length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateMessageId(MessageId) is { } messageViolation)
            return messageViolation;
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        if (Epoch < 0)
            return new(ProtocolViolationKind.Format, "The group message epoch must be nonnegative.");
        if (CreatedAt < 0)
            return new(ProtocolViolationKind.Time, "The group message creation time must be nonnegative.");
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The group message signature must contain 64 bytes.");
        if (Payload.Validate(context) is { } payloadViolation)
            return payloadViolation;
        try
        {
            return Encoding.UTF8.GetByteCount(ToJson()) <= 262144
                ? null
                : new(ProtocolViolationKind.Format, "The group message envelope cannot exceed 262144 canonical JSON bytes.");
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
    }
}
