using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Identifies groups for a relay notification subscription operation.
/// </summary>
public sealed record GroupSubscriptionRequest : ProtocolModel
{
    /// <summary>
    /// The group identifiers affected by the subscription operation.
    /// </summary>
    public required ImmutableArray<string> GroupIds { get; init; }

    /// <summary>
    /// Validates the group identifier list and rejects duplicate subscriptions.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (GroupIds.IsDefault)
            return new(ProtocolViolationKind.Format, "Group subscriptions must be an initialized array.");

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var groupId in GroupIds)
        {
            if (Identifiers.ValidateGroupId(groupId) is { } groupViolation)
                return groupViolation;
            if (!groups.Add(groupId))
                return new(ProtocolViolationKind.Format, "Group subscriptions cannot contain duplicate identifiers.");
        }
        return null;
    }
}
