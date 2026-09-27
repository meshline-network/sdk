using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a group secret encrypted to a member's encryption key using HPKE.
/// </summary>
public sealed record GroupSecretBox : ProtocolModel
{
    /// <summary>
    /// The HPKE suite identifier <c>X25519-HKDF-SHA256-AES256GCM</c>.
    /// </summary>
    public required string Alg { get; init; }
    /// <summary>
    /// The 32-byte HPKE encapsulated X25519 public key.
    /// </summary>
    public required ImmutableArray<byte> Enc { get; init; }
    /// <summary>
    /// The group secret encrypted with HPKE for the target member.
    /// </summary>
    public required ImmutableArray<byte> SealedSecret { get; init; }

    /// <summary>
    /// Validates the HPKE suite, encapsulated key, and encrypted group-secret size.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Alg != "X25519-HKDF-SHA256-AES256GCM")
            return new(ProtocolViolationKind.Unsupported, "Group secret boxes must use X25519-HKDF-SHA256-AES256GCM.");
        return Enc.AsSpan().Length != 32 || SealedSecret.AsSpan().Length != 60
            ? new(ProtocolViolationKind.Format, "A group secret box requires a 32-byte ephemeral public key and a 60-byte sealed secret.")
            : null;
    }
}
