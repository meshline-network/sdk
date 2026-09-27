using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Wraps a channel timeline payload with its sequence, descriptor revision, and signer information.
/// </summary>
public sealed record ChannelEvent : ProtocolModel
{
    /// <summary>
    /// The timeline sequence assigned by the hosting relay.
    /// </summary>
    public required long Sequence { get; init; }
    /// <summary>
    /// The channel descriptor revision governing this event.
    /// </summary>
    public required long DescriptorRev { get; init; }
    /// <summary>
    /// The typed descriptor, post, edit, or deletion payload carried by the event.
    /// </summary>
    public required TypedProtocolModel Payload { get; init; }
    /// <summary>
    /// The relay acceptance time, in Unix seconds.
    /// </summary>
    public required long AcceptedAt { get; init; }
    /// <summary>
    /// The identifier of the device that signed the associated payload.
    /// </summary>
    public required string SignerDeviceId { get; init; }

    /// <summary>
    /// Validates event metadata and descriptor, edit, and deletion sequence relationships.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>This does not validate the nested payload or verify its signature; the caller must validate and authorize it separately.</remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Sequence < 0 || DescriptorRev < 0)
            return new(ProtocolViolationKind.Format, "The channel sequence and descriptor revision must be nonnegative.");
        if (AcceptedAt < 0)
            return new(ProtocolViolationKind.Time, "The channel event acceptance time must be nonnegative.");
        if (Identifiers.ValidateDeviceId(SignerDeviceId) is { } deviceViolation)
            return deviceViolation;
        if (Sequence == 0 && Payload is not ChannelDescriptor { Revision: 0 })
            return new(ProtocolViolationKind.Conflict, "Sequence zero must contain the initial channel descriptor.");
        if (Payload is ChannelDescriptor descriptor)
        {
            if (DescriptorRev != descriptor.Revision)
                return new(ProtocolViolationKind.Conflict, "The event revision must match its channel descriptor.");
            if (Sequence > 0 && descriptor.Revision == 0)
                return new(ProtocolViolationKind.Conflict, "The initial channel descriptor must occur at sequence zero.");
        }
        if (Payload is ChannelPostEdit edit && (edit.TargetSequence <= 0 || edit.TargetSequence >= Sequence)
            || Payload is ChannelPostDelete deletion && (deletion.TargetSequence <= 0 || deletion.TargetSequence >= Sequence))
            return new(ProtocolViolationKind.Conflict, "A channel edit or deletion must reference an earlier positive sequence.");

        return null;
    }
}
