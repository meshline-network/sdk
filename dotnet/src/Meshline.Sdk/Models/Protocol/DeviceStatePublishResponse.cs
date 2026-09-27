using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Reports whether a device state was accepted or temporarily staged by a relay.
/// </summary>
public sealed record DeviceStatePublishResponse : ProtocolModel
{
    /// <summary>
    /// Whether the submitted state was accepted as authoritative or only staged.
    /// </summary>
    public required DeviceStatePublishStatus Status { get; init; }
    /// <summary>
    /// The expiration time of the temporarily staged device state, in Unix seconds, or <see langword="null"/> when unavailable.
    /// </summary>
    public long? StagedUntil { get; init; }

    /// <summary>
    /// Validates the publication status and whether staging expiry is present when required.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) => Status switch
    {
        DeviceStatePublishStatus.Accepted when StagedUntil is null => null,
        DeviceStatePublishStatus.Staged when StagedUntil is > 0 => null,
        DeviceStatePublishStatus.Accepted => new(ProtocolViolationKind.Format, "An accepted state must omit staged_until."),
        DeviceStatePublishStatus.Staged => new(ProtocolViolationKind.Format, "A staged state must include a positive staged_until."),
        _ => new(ProtocolViolationKind.Format, "Unknown device state publication status.")
    };
}
