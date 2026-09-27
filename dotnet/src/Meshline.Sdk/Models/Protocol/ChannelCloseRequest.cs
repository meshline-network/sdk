using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Carries the revision, update time, and signature used to close a channel.
/// </summary>
public sealed record ChannelCloseRequest : ProtocolModel
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId { get; init; }
    /// <summary>
    /// The monotonically increasing revision of the document.
    /// </summary>
    public required long Revision { get; init; }
    /// <summary>
    /// The last update time, in Unix seconds.
    /// </summary>
    public required long UpdatedAt { get; init; }
    /// <summary>
    /// The device signature over the model's device signing input.
    /// </summary>
    public required ImmutableArray<byte> DeviceSignature { get; init; }
}
