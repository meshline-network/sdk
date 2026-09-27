namespace Meshline.Components;

/// <summary>
/// Identifies how a contact request changed.
/// </summary>
public enum ContactRequestChangeKind
{
    /// <summary>
    /// A new contact request was stored.
    /// </summary>
    Added,
    /// <summary>
    /// An existing contact request changed.
    /// </summary>
    Updated,
    /// <summary>
    /// A contact request was removed.
    /// </summary>
    Removed
}
