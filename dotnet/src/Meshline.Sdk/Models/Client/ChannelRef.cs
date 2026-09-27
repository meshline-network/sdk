using Meshline.Identity;
using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Identifies a channel and the relay hosting it.
/// </summary>
public sealed record ChannelRef
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId
    {
        get;
        init
        {
            if (Identifiers.ValidateChannelId(value) is { } violation)
                throw new ArgumentException(violation.Message, nameof(ChannelId));
            field = value;
        }
    }

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
}
