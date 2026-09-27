using Meshline.Identity;
using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Transfers group ownership to another member through the management chain.
/// </summary>
public sealed record GroupOwnerTransfer() : GroupManagementOperation("meshline.group.owner.transfer")
{
    /// <summary>
    /// The CAIP-10 account identifier of the member receiving ownership.
    /// </summary>
    public required string NewOwnerAccount { get; init; }

    /// <summary>
    /// Validates management-chain fields and the new owner's account identifier.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (base.Validate(context) is { } violation)
            return violation;
        try
        {
            return AccountAdapter.ValidateAccountId(NewOwnerAccount);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
