namespace Meshline.Models.Protocol;

/// <summary>
/// Announces the current head of a group timeline.
/// </summary>
public sealed record GroupTimelineChangedNotification : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The newest timeline sequence advertised by the relay.
    /// </summary>
    public required long Head { get; init; }
}
