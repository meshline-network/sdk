using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Bans the listed accounts through the group's signed management chain.
/// </summary>
public sealed record GroupMemberBan() : GroupManagementOperation("meshline.group.member.ban")
{
    /// <summary>
    /// The account identifiers affected by the operation.
    /// </summary>
    public required ImmutableArray<string> Accounts { get; init; }

    /// <summary>
    /// Validates the management-chain fields and accounts to ban.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        base.Validate(context) ?? ValidateAccounts(Accounts);
}
