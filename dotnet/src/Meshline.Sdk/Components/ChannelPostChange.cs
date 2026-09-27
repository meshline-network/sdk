using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Describes a locally observed addition, edit, or deletion of a channel post.
/// </summary>
public sealed class ChannelPostChange
{
    /// <summary>
    /// The original channel post affected by the change.
    /// </summary>
    public required ChannelPostRef Ref { get; init; }
    /// <summary>
    /// The kind or combination of changes reported by this event.
    /// </summary>
    public ChannelPostChangeKind ChangeKind { get; init; }
    /// <summary>
    /// The post content after the change, or <see langword="null"/> for deletion.
    /// </summary>
    public ChannelPostInfo? Info { get; init; }
}
