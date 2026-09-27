using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Identifies channels for a relay notification subscription operation.
/// </summary>
public sealed record ChannelSubscriptionRequest : ProtocolModel
{
    /// <summary>
    /// The channel identifiers affected by the subscription operation.
    /// </summary>
    public required ImmutableArray<string> ChannelIds { get; init; }
}
