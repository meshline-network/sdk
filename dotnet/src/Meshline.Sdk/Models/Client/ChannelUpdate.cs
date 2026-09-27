namespace Meshline.Models.Client;

/// <summary>
/// Describes metadata field updates for a channel.
/// </summary>
public sealed class ChannelUpdate
{
    /// <summary>
    /// The update to the display name; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<string> Name { get; set; }
    /// <summary>
    /// The update to the description; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<string> Description { get; set; }
    /// <summary>
    /// The update to the moderator account list; the default value leaves the field unchanged.
    /// </summary>
    public FieldUpdate<IReadOnlyList<string>> Moderators { get; set; }
}
