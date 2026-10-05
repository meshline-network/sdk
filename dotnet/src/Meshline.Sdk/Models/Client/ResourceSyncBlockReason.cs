namespace Meshline.Models.Client;

/// <summary>
/// Identifies why synchronization cannot currently advance.
/// </summary>
public enum ResourceSyncBlockReason
{
    /// <summary>
    /// Relay communication prevents synchronization.
    /// </summary>
    Connection,
    /// <summary>
    /// An authenticated relay session is unavailable.
    /// </summary>
    Authentication,
    /// <summary>
    /// Required authorization is unavailable or insufficient.
    /// </summary>
    Permission,
    /// <summary>
    /// Required decryption key material is unavailable.
    /// </summary>
    MissingKey,
    /// <summary>
    /// Protocol data or cryptographic evidence could not be verified.
    /// </summary>
    Verification,
    /// <summary>
    /// Local persistence prevents synchronization.
    /// </summary>
    Storage,
    /// <summary>
    /// Required history is no longer available from the relay.
    /// </summary>
    HistoryUnavailable,
    /// <summary>
    /// Synchronization failed for a reason not classified by this SDK; inspect the original error.
    /// </summary>
    Unknown
}
