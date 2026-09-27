using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Reports how many member boxes were prepared and when the preparation expires.
/// </summary>
public sealed record GroupRotationPrepareResult : ProtocolModel
{
    /// <summary>
    /// The number of distinct current members with stored, structurally valid client-secret boxes; this does not prove the relay decrypted or verified their content.
    /// </summary>
    public required long Prepared { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }

    /// <summary>
    /// Validates that the rotation preparation has not expired.
    /// </summary>
    /// <param name="context">The optional network context; this time check does not depend on it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented check passes.</returns>
    /// <remarks>Prepared-member counts and expiration consistency across batches must be checked against the rotation separately.</remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()
            ? new(ProtocolViolationKind.Time, "The relay returned an invalid or changed rotation preparation interval.")
            : null;
}
