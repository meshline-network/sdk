namespace Meshline.Models.Registry;

/// <summary>
/// Indicates the relay's registration status in the registry.
/// </summary>
public enum RelayStatus
{
    /// <summary>
    /// The registry lists the relay as active.
    /// </summary>
    Active,
    /// <summary>
    /// The registry lists the relay as disabled.
    /// </summary>
    Disabled,
    /// <summary>
    /// The registry lists the relay as suspended.
    /// </summary>
    Suspended
}
