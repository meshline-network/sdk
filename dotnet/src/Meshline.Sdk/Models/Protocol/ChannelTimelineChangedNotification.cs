namespace Meshline.Models.Protocol;

/// <summary>
/// Announces the current head of a channel timeline.
/// </summary>
public sealed record ChannelTimelineChangedNotification : ProtocolModel
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId { get; init; }
    /// <summary>
    /// The newest timeline sequence advertised by the relay.
    /// </summary>
    public required long Head { get; init; }
}
