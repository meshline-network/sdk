using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Transfers private group membership state between devices of the same account.
/// </summary>
public sealed record AccountGroupPrivateStateSync() : TypedProtocolModel("meshline.account.group.state.sync")
{
    /// <summary>
    /// The private group membership states included in this synchronization message.
    /// </summary>
    public required ImmutableArray<GroupMemberPrivateState> States { get; init; }

    /// <summary>
    /// Validates private membership entries and rejects duplicate group identifiers.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (States.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "A private state synchronization batch must contain at least one state.");

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in States)
        {
            if (state is null)
                return new(ProtocolViolationKind.Format, "Private states cannot contain null.");
            if (state.Validate() is { } stateViolation)
                return stateViolation;
            if (!groups.Add(state.GroupId))
                return new(ProtocolViolationKind.Conflict, "A private state synchronization batch cannot contain duplicate groups.");
        }
        return null;
    }
}
