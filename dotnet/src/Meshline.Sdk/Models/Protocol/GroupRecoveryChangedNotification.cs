namespace Meshline.Models.Protocol;

/// <summary>
/// Announces a change to a group's pending member key recovery requests.
/// </summary>
public sealed record GroupRecoveryChangedNotification : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
}
