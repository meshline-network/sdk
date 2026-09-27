namespace Meshline.Components;

/// <summary>
/// Describes a device's authorization in the currently known account device state.
/// </summary>
public enum DeviceAuthorizationState
{
    /// <summary>
    /// No account device state is available for assessment.
    /// </summary>
    Unknown,
    /// <summary>
    /// The device is registered and its certificate is currently valid.
    /// </summary>
    Authorized,
    /// <summary>
    /// The device is absent from the known account device state.
    /// </summary>
    NotRegistered,
    /// <summary>
    /// The registered certificate's validity period has not begun.
    /// </summary>
    NotYetValid,
    /// <summary>
    /// The registered certificate has expired.
    /// </summary>
    Expired
}
