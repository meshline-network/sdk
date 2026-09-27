using Meshline.Validation;
using System.Security.Cryptography;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a member key recovery request and its relay-assigned validity window.
/// </summary>
public sealed record GroupRecoveryEntry : ProtocolModel
{
    /// <summary>
    /// The signed group member key recovery request.
    /// </summary>
    public required GroupMemberRecoveryRequest Request { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }
    /// <summary>
    /// The relay acceptance time, in Unix seconds.
    /// </summary>
    public required long AcceptedAt { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }

    /// <summary>
    /// Validates the relay-assigned validity window, signer certificate, and nested recovery request.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// This checks the validity-window structure, not whether the request has expired. Resource and account matching,
    /// request signature verification, and signer authorization must be checked separately.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform certificate signature verification.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (AcceptedAt < 0 || ExpiresAt <= AcceptedAt)
            return new(ProtocolViolationKind.Time, "The recovery request has invalid identity or acceptance fields.");
        return SignerCertificate.Validate(context) ?? Request.Validate(context);
    }
}
