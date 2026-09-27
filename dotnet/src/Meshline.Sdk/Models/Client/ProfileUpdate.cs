using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Describes field assignments and deletions to apply to the current account's profile.
/// </summary>
public sealed class ProfileUpdate
{
    /// <summary>
    /// The update to the profile nickname; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<string> Nickname { get; set; }
    /// <summary>
    /// The update to the avatar content reference; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<ContentReference> Avatar { get; set; }
    /// <summary>
    /// The update to the profile biography; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<string> Bio { get; set; }
    /// <summary>
    /// The update to the public discovery preference; the default value leaves it unchanged and deletion is not permitted.
    /// </summary>
    public FieldUpdate<bool> PublicDiscovery { get; set; }
}
