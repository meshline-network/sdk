using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Describes a change to a locally stored contact.
/// </summary>
public sealed class ContactChangedEventArgs : EventArgs
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public string AccountId { get; }
    /// <summary>
    /// The updated contact snapshot, or <see langword="null"/> when no snapshot accompanies the change.
    /// </summary>
    public ContactInfo? Contact { get; }
    /// <summary>
    /// The kind or combination of changes reported by this event.
    /// </summary>
    public ContactChangeKind ChangeKind { get; }

    internal ContactChangedEventArgs(string accountId, ContactInfo? contact, ContactChangeKind changeKind)
    {
        AccountId = accountId;
        Contact = contact;
        ChangeKind = changeKind;
    }
}
