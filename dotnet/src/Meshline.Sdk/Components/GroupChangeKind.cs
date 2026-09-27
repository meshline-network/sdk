namespace Meshline.Components;

/// <summary>
/// Identifies the aspects of group state that changed; values may be combined.
/// </summary>
[Flags]
public enum GroupChangeKind
{
    /// <summary>
    /// The group's name, description, capacity, or invitation policy changed.
    /// </summary>
    Properties = 1,
    /// <summary>
    /// Group membership changed.
    /// </summary>
    Members = 2,
    /// <summary>
    /// A member's role or group ownership changed.
    /// </summary>
    Roles = 4,
    /// <summary>
    /// The group's banned accounts changed.
    /// </summary>
    Bans = 8,
    /// <summary>
    /// A member's private group nickname changed.
    /// </summary>
    Nickname = 16,
    /// <summary>
    /// The group's lifecycle or local membership status changed.
    /// </summary>
    Status = 32
}
