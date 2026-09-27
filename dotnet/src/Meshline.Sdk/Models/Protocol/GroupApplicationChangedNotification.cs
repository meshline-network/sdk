namespace Meshline.Models.Protocol;

/// <summary>
/// Announces a change to a group's pending admission applications.
/// </summary>
public sealed record GroupApplicationChangedNotification : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
}
