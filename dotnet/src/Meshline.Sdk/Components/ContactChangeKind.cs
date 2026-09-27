namespace Meshline.Components;

/// <summary>
/// Identifies the aspects of a contact that changed; values may be combined.
/// </summary>
[Flags]
public enum ContactChangeKind
{
    /// <summary>
    /// The contact relationship changed.
    /// </summary>
    Relationship = 1,
    /// <summary>
    /// The private contact alias changed.
    /// </summary>
    Alias = 2,
    /// <summary>
    /// A contact grant or its authorization state changed.
    /// </summary>
    Authorization = 4,
    /// <summary>
    /// The contact was marked deleted.
    /// </summary>
    Deleted = 8,
}
