using Meshline.Validation;
using System.Security.Cryptography;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains an admission application, its signer certificate, and relay acceptance time.
/// </summary>
public sealed record GroupApplicationEntry : ProtocolModel
{
    /// <summary>
    /// The signed application to join the group.
    /// </summary>
    public required GroupApplication Application { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }
    /// <summary>
    /// The relay acceptance time, in Unix seconds.
    /// </summary>
    public required long AcceptedAt { get; init; }

    /// <summary>
    /// Validates the acceptance time, signer certificate, and nested admission application.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// Resource and account matching, application signature verification, and signer authorization must be checked separately.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform certificate signature verification.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (AcceptedAt < 0)
            return new(ProtocolViolationKind.Time, "The group application has invalid identity or acceptance fields.");
        return SignerCertificate.Validate(context) ?? Application.Validate(context);
    }
}
