using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Approves replacement member encryption keys for the listed group accounts.
/// </summary>
public sealed record GroupMemberRecoveryApproval() : GroupManagementOperation("meshline.group.member.recovery.approval")
{
    /// <summary>
    /// The accounts and replacement member encryption keys approved for recovery.
    /// </summary>
    public required ImmutableArray<GroupMemberKey> Members { get; init; }

    /// <summary>
    /// Validates management-chain fields and replacement member keys.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        base.Validate(context) ?? ValidateMembers(Members);
}
