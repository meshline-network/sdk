using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a relay-reported delivery state, acceptance time, and optional failure.
/// </summary>
public sealed record MessageDeliveryStatus : ProtocolModel
{
    /// <summary>
    /// The current relay-reported delivery state.
    /// </summary>
    public required MessageDeliveryState Status { get; init; }
    /// <summary>
    /// The error associated with this result or diagnostic.
    /// </summary>
    public RelayError? Error { get; init; }
    /// <summary>
    /// The relay acceptance time, in Unix seconds.
    /// </summary>
    public required long AcceptedAt { get; init; }

    /// <summary>
    /// Validates the delivery state, acceptance time, and presence of an error for failed delivery.
    /// </summary>
    /// <param name="context">The optional network context; these field-only checks do not require it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (!Enum.IsDefined(Status) || AcceptedAt < 0 || AcceptedAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || (Status == MessageDeliveryState.Failed) != (Error is not null))
            return new(ProtocolViolationKind.Format, "The relay returned an inconsistent delivery result.");
        return null;
    }
}
