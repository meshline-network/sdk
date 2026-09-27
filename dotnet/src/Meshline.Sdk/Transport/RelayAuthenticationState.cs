namespace Meshline.Transport;

/// <summary>
/// Describes the observed authentication state of a relay client.
/// </summary>
public enum RelayAuthenticationState
{
    /// <summary>
    /// No authenticated session has been established.
    /// </summary>
    None,
    /// <summary>
    /// Authentication is in progress.
    /// </summary>
    Authenticating,
    /// <summary>
    /// A valid account-authorized session is available.
    /// </summary>
    Account,
    /// <summary>
    /// A valid device-authorized session is available.
    /// </summary>
    Device,
    /// <summary>
    /// The authenticated session has expired.
    /// </summary>
    Expired,
    /// <summary>
    /// Authentication was rejected by the relay.
    /// </summary>
    Rejected
}
