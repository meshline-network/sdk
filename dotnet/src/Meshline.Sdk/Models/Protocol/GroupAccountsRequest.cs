using Meshline.Identity;
using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Selects a set of accounts for an operation on one group.
/// </summary>
public sealed record GroupAccountsRequest : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The account identifiers affected by the operation.
    /// </summary>
    public required ImmutableArray<string> Accounts { get; init; }

    /// <summary>
    /// Validates the group identifier and affected account list.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        if (Accounts.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "The account list must not be empty.");

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var account in Accounts)
            {
                if (account is null)
                    return new(ProtocolViolationKind.Format, "The account list cannot contain null.");
                if (AccountAdapter.ValidateAccountId(account) is { } accountViolation)
                    return accountViolation;
                if (!accounts.Add(account))
                    return new(ProtocolViolationKind.Format, "The account list cannot contain duplicates.");
            }
            return null;
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
