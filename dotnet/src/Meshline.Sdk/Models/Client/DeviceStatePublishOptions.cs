using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Controls the prior state, recovery mode, and revision used when publishing device authorization.
/// </summary>
public sealed class DeviceStatePublishOptions
{
    /// <summary>
    /// Optional prior device state used when preserving certificates and selecting the next revision.
    /// </summary>
    public AccountDeviceState? PreviousState { get; set; }
    /// <summary>
    /// Whether publication explicitly recovers the account and may restore missing local-device authorization.
    /// </summary>
    public bool IsRecovery { get; set; }
    /// <summary>
    /// An explicit newer device-state revision, or <see langword="null"/> for automatic selection.
    /// </summary>
    public long? Revision { get; set; }
}
