using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Contains the submitted device state and whether the relay accepted or temporarily staged it.
/// </summary>
public sealed class DeviceStatePublishResult
{
    /// <summary>
    /// The complete device state submitted to the relay; inspect the status before treating it as authoritative.
    /// </summary>
    public required AccountDeviceState DeviceState { get; init; }
    /// <summary>
    /// Whether the submitted state was accepted as authoritative or only staged.
    /// </summary>
    public required DeviceStatePublishStatus Status { get; init; }
    /// <summary>
    /// The expiration time of the temporarily staged device state, or <see langword="null"/> when unavailable.
    /// </summary>
    public DateTimeOffset? StagedUntil { get; init; }
}
