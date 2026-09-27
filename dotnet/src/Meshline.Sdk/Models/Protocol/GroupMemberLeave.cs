using Meshline.Identity;
using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Records a member's departure through the group's signed management chain.
/// </summary>
public sealed record GroupMemberLeave() : GroupManagementOperation("meshline.group.member.leave")
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }

    /// <summary>
    /// Validates the management-chain fields and departing account identifier.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (base.Validate(context) is { } violation)
            return violation;
        try
        {
            return AccountAdapter.ValidateAccountId(Account);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
