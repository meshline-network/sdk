using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Synchronizes contact records between devices of the same account or requests a snapshot.
/// </summary>
public sealed record AccountContactSync() : TypedProtocolModel("meshline.account.contacts.sync")
{
    /// <summary>
    /// The contact records included in this synchronization message.
    /// </summary>
    public ImmutableArray<ContactRecord> Records { get; init; } = [];
    /// <summary>
    /// Whether the receiving account device should send a complete contact snapshot.
    /// </summary>
    public bool? RequestSnapshot { get; init; }

    /// <summary>
    /// Validates contact-record structure, unique accounts, relationship fields, and grant direction.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Records.IsDefault)
            return new(ProtocolViolationKind.Format, "Contact records must be an initialized array when present.");

        try
        {
            var accounts = new HashSet<string>(StringComparer.Ordinal);
            string? owner = null;
            foreach (var record in Records)
            {
                if (record is null)
                    return new(ProtocolViolationKind.Format, "Contact records cannot contain null.");
                if (record.Validate(context) is { } recordViolation)
                    return recordViolation;
                if (!accounts.Add(record.Account))
                    return new(ProtocolViolationKind.Conflict, "A contact synchronization batch cannot contain duplicate accounts.");

                if (record.GrantFromContact is { } fromContact)
                {
                    if (fromContact.Validate(context) is { } grantViolation)
                        return grantViolation;
                    if (fromContact.Grantor != record.Account || owner is not null && fromContact.Grantee != owner)
                        return new(ProtocolViolationKind.Identity, "An incoming contact grant must be issued by the contact to the synchronized account.");
                    owner = fromContact.Grantee;
                }
                if (record.GrantToContact is { } toContact)
                {
                    if (toContact.Validate(context) is { } grantViolation)
                        return grantViolation;
                    if (toContact.Grantee != record.Account || owner is not null && toContact.Grantor != owner)
                        return new(ProtocolViolationKind.Identity, "An outgoing contact grant must be issued by the synchronized account to the contact.");
                    owner = toContact.Grantor;
                }
            }

            return owner is not null && accounts.Contains(owner)
                ? new(ProtocolViolationKind.Identity, "Contact records cannot include the synchronized account itself.")
                : null;
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
