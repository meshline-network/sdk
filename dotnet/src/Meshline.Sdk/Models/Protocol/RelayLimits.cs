using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains relay retention periods and optional channel and group limits.
/// </summary>
public sealed record RelayLimits : ProtocolModel
{
    /// <summary>
    /// The account-message retention period, in seconds.
    /// </summary>
    public required long MessageRetention { get; init; }
    /// <summary>
    /// The channel timeline retention period, in seconds, when advertised.
    /// </summary>
    public long? ChannelTimelineRetention { get; init; }
    /// <summary>
    /// The maximum simultaneous channel subscriptions, when advertised.
    /// </summary>
    public long? MaxChannelSubscriptions { get; init; }
    /// <summary>
    /// The group-message retention period, in seconds, when advertised.
    /// </summary>
    public long? GroupMessageRetention { get; init; }
    /// <summary>
    /// The maximum permitted group membership count, when advertised.
    /// </summary>
    public long? MaxGroupMembers { get; init; }
    /// <summary>
    /// The maximum simultaneous group subscriptions, when advertised.
    /// </summary>
    public long? MaxGroupSubscriptions { get; init; }
    /// <summary>
    /// The maximum group invitation lifetime, in seconds, when advertised.
    /// </summary>
    public long? MaxGroupInviteTtl { get; init; }

    /// <summary>
    /// Validates positive retention, capacity, and invitation-lifetime limits and nonnegative subscription limits.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (MessageRetention <= 0 || ChannelTimelineRetention is <= 0 || GroupMessageRetention is <= 0
            || MaxGroupMembers is <= 0 || MaxGroupInviteTtl is <= 0)
            return new(ProtocolViolationKind.Format, "Retention periods, group capacity and invitation lifetime must be positive when present.");
        return MaxChannelSubscriptions is < 0 || MaxGroupSubscriptions is < 0
            ? new(ProtocolViolationKind.Format, "Subscription limits must be nonnegative when present.")
            : null;
    }
}
