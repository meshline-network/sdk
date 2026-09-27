using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Approves group admission for the listed accounts and their member encryption keys.
/// </summary>
public sealed record GroupApplicationApproval() : GroupManagementOperation("meshline.group.application.approval")
{
    /// <summary>
    /// The accounts and member encryption keys approved for admission.
    /// </summary>
    public required ImmutableArray<GroupMemberKey> Members { get; init; }

    /// <summary>
    /// Validates management-chain fields and the approved member-key list.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        base.Validate(context) ?? ValidateMembers(Members);
}
