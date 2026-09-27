using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Reports when a submitted member key recovery request was accepted and will expire.
/// </summary>
public sealed record GroupRecoverySubmitResult : ProtocolModel
{
    /// <summary>
    /// The relay acceptance time, in Unix seconds.
    /// </summary>
    public required long AcceptedAt { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }

    /// <summary>
    /// Validates the relay-assigned recovery acceptance interval.
    /// </summary>
    /// <param name="context">The optional network context; these field checks do not depend on it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>This checks the interval structure, not whether the request has expired.</remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        AcceptedAt < 0 || ExpiresAt <= AcceptedAt
            ? new(ProtocolViolationKind.Time, "The relay returned an invalid recovery acceptance interval.")
            : null;
}
