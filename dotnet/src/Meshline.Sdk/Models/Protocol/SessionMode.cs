namespace Meshline.Models.Protocol;

/// <summary>
/// Distinguishes account-authorized sessions from device-authorized sessions.
/// </summary>
public enum SessionMode
{
    /// <summary>
    /// The session authenticates a specific authorized device.
    /// </summary>
    Device,
    /// <summary>
    /// The session authenticates the account through its account signer.
    /// </summary>
    Account
}
