using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Reports a channel post to its hosting relay with a reason.
/// </summary>
public sealed record ChannelReportRequest : ProtocolModel
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId { get; init; }
    /// <summary>
    /// The original timeline sequence of the channel post being modified or reported.
    /// </summary>
    public required long TargetSequence { get; init; }
    /// <summary>
    /// The human-readable reason associated with the operation.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Validates the reported post reference and report reason.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateChannelId(ChannelId) is { } channelViolation)
            return channelViolation;
        if (TargetSequence <= 0)
            return new(ProtocolViolationKind.Format, "The reported post sequence must be positive.");
        return string.IsNullOrWhiteSpace(Reason)
            ? new(ProtocolViolationKind.Format, "The report reason must contain non-whitespace text.")
            : null;
    }
}
