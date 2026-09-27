using Meshline.Identity;
using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Identifies a group and the relay hosting it.
/// </summary>
public sealed record GroupRef
{
    /// <summary>
    /// The relay's lowercase Neo script hash, including the <c>0x</c> prefix.
    /// </summary>
    public required string RelayId
    {
        get;
        init
        {
            if (RelayIdentity.ValidateRelayId(value) is { } violation)
                throw new ArgumentException(violation.Message, nameof(RelayId));
            field = value;
        }
    }

    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId
    {
        get;
        init
        {
            if (Identifiers.ValidateGroupId(value) is { } violation)
                throw new ArgumentException(violation.Message, nameof(GroupId));
            field = value;
        }
    }
}
