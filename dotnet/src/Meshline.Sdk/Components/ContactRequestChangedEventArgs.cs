using Meshline.Models.Client;

namespace Meshline.Components;

/// <summary>
/// Describes a change to an incoming or outgoing contact request.
/// </summary>
public sealed class ContactRequestChangedEventArgs : EventArgs
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public string AccountId { get; }
    /// <summary>
    /// Whether the contact request is incoming or outgoing.
    /// </summary>
    public ContactRequestDirection Direction { get; }
    /// <summary>
    /// The updated request snapshot, or <see langword="null"/> when the request was removed.
    /// </summary>
    public ContactRequestInfo? Request { get; }
    /// <summary>
    /// The kind or combination of changes reported by this event.
    /// </summary>
    public ContactRequestChangeKind ChangeKind { get; }

    internal ContactRequestChangedEventArgs(string accountId, ContactRequestDirection direction, ContactRequestInfo? request, ContactRequestChangeKind changeKind)
    {
        AccountId = accountId;
        Direction = direction;
        Request = request;
        ChangeKind = changeKind;
    }
}
