namespace Meshline.Models.Protocol;

/// <summary>
/// Selects the latest, preceding, or following page of a channel timeline.
/// </summary>
public sealed record ChannelReadQuery : ProtocolModel
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId { get; init; }
    /// <summary>
    /// An exclusive upper sequence bound for reading older channel events; cannot be combined with <c>After</c>.
    /// </summary>
    public long? Before { get; init; }
    /// <summary>
    /// An exclusive lower sequence bound for forward synchronization.
    /// </summary>
    public long? After { get; init; }
    /// <summary>
    /// The requested maximum page size, or <see langword="null"/> to use the operation's default.
    /// </summary>
    public long? Limit { get; init; }
}
