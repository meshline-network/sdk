namespace Meshline.Models.Client;

/// <summary>
/// Describes the local assessment of a contact grant's usability.
/// </summary>
public enum ContactGrantState
{
    /// <summary>
    /// The grant cannot yet be assessed from the available evidence.
    /// </summary>
    Unknown,
    /// <summary>
    /// No grant is available.
    /// </summary>
    Missing,
    /// <summary>
    /// The grant has usable authorization evidence and is currently valid.
    /// </summary>
    Valid,
    /// <summary>
    /// The grant's validity period has ended.
    /// </summary>
    Expired,
    /// <summary>
    /// The grant lacks sufficient signatures from currently authorized devices.
    /// </summary>
    InsufficientSignatures
}
