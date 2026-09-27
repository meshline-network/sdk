using Meshline.Validation;
using System.Security.Cryptography;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a channel descriptor and its signer's device certificate.
/// </summary>
public sealed record ChannelResolveResult : ProtocolModel
{
    /// <summary>
    /// The resolved signed channel descriptor.
    /// </summary>
    public required ChannelDescriptor Descriptor { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }

    /// <summary>
    /// Validates the nested channel descriptor and signer certificate.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// Resource and account matching, descriptor signature verification, and signer authorization must be checked separately.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform certificate signature verification.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context) =>
        Descriptor.Validate(context) ?? SignerCertificate.Validate(context);
}
