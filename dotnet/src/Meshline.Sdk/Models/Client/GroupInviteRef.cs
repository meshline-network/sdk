using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Identifies a hosted group invitation without embedding its signed document.
/// </summary>
public sealed record GroupInviteRef
{
    /// <summary>
    /// The group and hosting relay associated with this information.
    /// </summary>
    public GroupRef Group { get; }
    /// <summary>
    /// The invitation's canonical <c>inv_</c> identifier.
    /// </summary>
    public string InviteId { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="GroupInviteRef"/>.
    /// </summary>
    /// <param name="group">The group identifier and hosting relay.</param>
    /// <param name="inviteId">The canonical group invitation identifier.</param>
    /// <exception cref="ArgumentNullException">The <paramref name="group"/> argument is null.</exception>
    /// <exception cref="ArgumentException">The invitation identifier is invalid.</exception>
    public GroupInviteRef(GroupRef group, string inviteId)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (Identifiers.ValidateInviteId(inviteId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(inviteId));
        Group = group;
        InviteId = inviteId;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="GroupInviteRef"/>.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="groupId">The canonical group identifier.</param>
    /// <param name="inviteId">The canonical group invitation identifier.</param>
    /// <exception cref="ArgumentException">The invitation identifier is invalid, or the supplied relay or group identifier is invalid.</exception>
    public GroupInviteRef(string relayId, string groupId, string inviteId)
        : this(new() { RelayId = relayId, GroupId = groupId }, inviteId)
    {
    }
}
