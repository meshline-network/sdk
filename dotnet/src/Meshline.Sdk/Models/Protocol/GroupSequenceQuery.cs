using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Selects group records after an exclusive sequence or epoch cursor.
/// </summary>
public sealed record GroupSequenceQuery : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// An exclusive lower cursor: a timeline sequence for synchronization or an epoch for key retrieval.
    /// </summary>
    public long? After { get; init; }
    /// <summary>
    /// The requested maximum page size, or <see langword="null"/> to use the operation's default.
    /// </summary>
    public long? Limit { get; init; }

    /// <summary>
    /// Validates the group identifier, lower cursor bound, and page limit.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        if (After is < -1)
            return new(ProtocolViolationKind.Format, "The synchronization position must be at least -1 when specified.");
        return Limit is <= 0
            ? new(ProtocolViolationKind.Format, "The page limit must be positive when specified.")
            : null;
    }
}
