namespace Meshline.Models.Protocol;

/// <summary>
/// Reports the timeline sequence assigned to an accepted operation.
/// </summary>
public sealed record SequenceResult : ProtocolModel
{
    /// <summary>
    /// The timeline sequence assigned by the hosting relay.
    /// </summary>
    public required long Sequence { get; init; }
}
