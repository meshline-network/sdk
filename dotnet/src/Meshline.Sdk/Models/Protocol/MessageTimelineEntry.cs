namespace Meshline.Models.Protocol;

/// <summary>
/// Contains an accepted account message and the key box for the requesting device.
/// </summary>
public sealed record MessageTimelineEntry : ProtocolModel
{
    /// <summary>
    /// The timeline sequence assigned by the hosting relay.
    /// </summary>
    public required long Sequence { get; init; }
    /// <summary>
    /// The signed encrypted message envelope.
    /// </summary>
    public required MessageEnvelope Envelope { get; init; }
    /// <summary>
    /// The encrypted message key box intended for the requesting device.
    /// </summary>
    public required MessageKeyBox KeyBox { get; init; }
    /// <summary>
    /// The relay acceptance time, in Unix seconds.
    /// </summary>
    public required long AcceptedAt { get; init; }
}
