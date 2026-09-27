using Meshline.Identity;
using Meshline.Validation;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Carries a contact relationship and its grants for synchronization between account devices.
/// </summary>
public sealed record ContactRecord : ProtocolModel
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The optional private alias assigned to the contact.
    /// </summary>
    public string? Alias { get; init; }
    /// <summary>
    /// The synchronized contact relationship state.
    /// </summary>
    public required ContactRelationshipState Status { get; init; }
    /// <summary>
    /// The contact's grant permitting this account to contact it, when available.
    /// </summary>
    public ContactGrant? GrantFromContact { get; init; }
    /// <summary>
    /// This account's grant permitting the contact to send messages, when available.
    /// </summary>
    public ContactGrant? GrantToContact { get; init; }
    /// <summary>
    /// The last update time, in nonnegative Unix seconds, no later than <see cref="DateTimeOffset.MaxValue"/> and at most five minutes ahead of the local clock when validated.
    /// </summary>
    public required long UpdatedAt { get; init; }

    /// <summary>
    /// Validates the contact account, update time, relationship state, alias, and deleted-record grant restrictions.
    /// </summary>
    /// <param name="context">The optional network context; these field checks do not depend on it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>Grant validation, grant direction, and synchronization-batch consistency must be checked separately.</remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        try
        {
            if (AccountAdapter.ValidateAccountId(Account) is { } accountViolation)
                return accountViolation;
            if (UpdatedAt < 0)
                return new(ProtocolViolationKind.Time, "The contact update time must be nonnegative.");
            if (UpdatedAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                return new(ProtocolViolationKind.Time, "The contact update time exceeds the supported date range.");
            if (UpdatedAt > Clock.UtcNow.ToUnixTimeSeconds() + 300)
                return new(ProtocolViolationKind.Time, "The contact update time is more than five minutes ahead of the local clock.");
            if (!Enum.IsDefined(Status))
                return new(ProtocolViolationKind.Format, "The contact relationship state is invalid.");
            if (Alias is { Length: > 0 } alias && (string.IsNullOrWhiteSpace(alias) || Encoding.UTF8.GetByteCount(alias) > 256))
                return new(ProtocolViolationKind.Format, "A nonempty contact alias must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.");
            if (Status == ContactRelationshipState.Deleted && (GrantFromContact is not null || GrantToContact is not null))
                return new(ProtocolViolationKind.Format, "A deleted contact record cannot contain grants.");
            return null;
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
