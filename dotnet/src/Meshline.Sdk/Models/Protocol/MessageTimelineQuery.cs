namespace Meshline.Models.Protocol;

/// <summary>
/// Selects account messages after an exclusive timeline sequence.
/// </summary>
public sealed record MessageTimelineQuery : ProtocolModel
{
    /// <summary>
    /// An exclusive lower sequence bound for forward synchronization.
    /// </summary>
    public long? After { get; init; }
    /// <summary>
    /// The requested maximum page size, or <see langword="null"/> to use the operation's default.
    /// </summary>
    public long? Limit { get; init; }
}
