using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Contains a contact's relationship, local alias, and authorization state in both directions.
/// </summary>
public sealed class ContactInfo
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string AccountId { get; init; }
    /// <summary>
    /// The optional private alias assigned to the contact.
    /// </summary>
    public string? Alias { get; init; }
    /// <summary>
    /// The current contact relationship state.
    /// </summary>
    public required ContactRelationshipState State { get; init; }
    /// <summary>
    /// Whether the contact's grant currently permits sending messages to the contact.
    /// </summary>
    public required ContactGrantState GrantFromContact { get; init; }
    /// <summary>
    /// Whether this account's grant currently permits the contact to send messages to it.
    /// </summary>
    public required ContactGrantState GrantToContact { get; init; }
    /// <summary>
    /// The last update time.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
