namespace Meshline.Models.Protocol;

/// <summary>
/// Indicates whether published device state is authoritative or only staged.
/// </summary>
public enum DeviceStatePublishStatus
{
    /// <summary>
    /// The relay accepted the device state as authoritative.
    /// </summary>
    Accepted,
    /// <summary>
    /// The relay temporarily staged the device state pending account route establishment or migration.
    /// </summary>
    Staged
}
