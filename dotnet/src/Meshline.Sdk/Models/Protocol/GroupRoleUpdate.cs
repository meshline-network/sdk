using Meshline.Identity;
using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Changes a member's role through the group's signed management chain.
/// </summary>
public sealed record GroupRoleUpdate() : GroupManagementOperation("meshline.group.role.update")
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The account's group role.
    /// </summary>
    public required GroupRole Role { get; init; }

    /// <summary>
    /// Validates management-chain fields, the member account, and an administrator or ordinary-member role assignment.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (base.Validate(context) is { } violation)
            return violation;
        if (Role == GroupRole.Owner)
            return new(ProtocolViolationKind.Format, "A role update can only assign administrator or member.");
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
