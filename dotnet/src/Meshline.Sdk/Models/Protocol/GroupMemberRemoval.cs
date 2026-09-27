using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Removes the listed accounts from group membership.
/// </summary>
public sealed record GroupMemberRemoval() : GroupManagementOperation("meshline.group.member.removal")
{
    /// <summary>
    /// The account identifiers affected by the operation.
    /// </summary>
    public required ImmutableArray<string> Accounts { get; init; }

    /// <summary>
    /// Validates management-chain fields and the accounts to remove.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        base.Validate(context) ?? ValidateAccounts(Accounts);
}
