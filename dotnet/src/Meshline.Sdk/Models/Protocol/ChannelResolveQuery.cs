namespace Meshline.Models.Protocol;

/// <summary>
/// Selects a channel descriptor, optionally at a specific revision.
/// </summary>
public sealed record ChannelResolveQuery : ProtocolModel
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId { get; init; }
    /// <summary>
    /// The descriptor revision to resolve, or <see langword="null"/> for the current descriptor.
    /// </summary>
    public long? Revision { get; init; }
}
