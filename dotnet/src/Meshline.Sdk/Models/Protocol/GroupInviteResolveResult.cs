using Meshline.Validation;
using System.Security.Cryptography;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a resolved group invitation, its signer certificate, and use count.
/// </summary>
public sealed record GroupInviteResolveResult : ProtocolModel
{
    /// <summary>
    /// The resolved signed group invitation document.
    /// </summary>
    public required GroupInvite Invite { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }
    /// <summary>
    /// The invitation use count reported by the relay.
    /// </summary>
    public required long Uses { get; init; }

    /// <summary>
    /// Validates the signer certificate, invitation use count, and nested invitation.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// Resource and account matching, invitation signature verification, and signer authorization must be checked separately.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform certificate signature verification.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        if (SignerCertificate.Validate(context) is { } certificateViolation) return certificateViolation;
        if (Uses < 0 || Invite.Invitee is not null && Uses > 1 || Invite.MaxUses is { } maximum && Uses > maximum)
            return new(ProtocolViolationKind.Format, "The group invitation has invalid identity or usage fields.");
        return Invite.Validate(context);
    }
}
