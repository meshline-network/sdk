namespace Meshline.Models.Protocol;

/// <summary>
/// Announces the current head of the account message timeline.
/// </summary>
public sealed record MessageTimelineChangedNotification : ProtocolModel
{
    /// <summary>
    /// The newest timeline sequence advertised by the relay.
    /// </summary>
    public required long Head { get; init; }
}
