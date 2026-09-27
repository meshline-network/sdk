using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Combines a channel reference with its known descriptor and local following state.
/// </summary>
public sealed class ChannelInfo
{
    /// <summary>
    /// The channel and hosting relay to which this information applies.
    /// </summary>
    public required ChannelRef Ref { get; init; }
    /// <summary>
    /// The known signed channel descriptor, or <see langword="null"/> when it is unavailable.
    /// </summary>
    public ChannelDescriptor? Descriptor { get; init; }
    /// <summary>
    /// Whether this device locally follows the channel.
    /// </summary>
    public bool IsFollowed { get; init; }
}
