using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Configures explicit account recovery, including prior device state and revision overrides.
/// </summary>
public sealed class AccountRecoveryOptions
{
    /// <summary>
    /// The target relay's canonical identifier, or <see langword="null"/> to prefer the resolved home relay and otherwise select an eligible registry relay.
    /// </summary>
    public string? RelayId { get; set; }
    /// <summary>
    /// The requested device certificate lifetime, from one second through 720 days; defaults to 365 days.
    /// </summary>
    public TimeSpan CertificateValidity { get; set; } = TimeSpan.FromDays(365);
    /// <summary>
    /// The requested account route lifetime, from one second through 3650 days; defaults to 365 days.
    /// </summary>
    public TimeSpan RouteValidity { get; set; } = TimeSpan.FromDays(365);
    /// <summary>
    /// Optional verified prior device state used to preserve devices and advance the recovery revision.
    /// </summary>
    public AccountDeviceState? PreviousDeviceState { get; set; }
    /// <summary>
    /// An explicit device-state revision, or <see langword="null"/> to select a recovery revision automatically.
    /// </summary>
    public long? DeviceStateRevision { get; set; }
    /// <summary>
    /// An explicit route revision, or <see langword="null"/> to select a recovery revision automatically.
    /// </summary>
    public long? RouteRevision { get; set; }
}
