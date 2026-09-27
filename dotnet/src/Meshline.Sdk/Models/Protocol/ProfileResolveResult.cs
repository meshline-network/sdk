using Meshline.Validation;
using System.Security.Cryptography;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains an account profile and its signer's device certificate.
/// </summary>
public sealed record ProfileResolveResult : ProtocolModel
{
    /// <summary>
    /// The resolved signed account profile.
    /// </summary>
    public required AccountProfile Profile { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }

    /// <summary>
    /// Validates the nested account profile and signer certificate.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// Account matching, profile signature verification, and signer authorization must be checked separately.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform certificate signature verification.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Profile.Validate(context) ?? SignerCertificate.Validate(context);
    }
}
