using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains an authenticated-encryption algorithm, nonce, and ciphertext.
/// </summary>
public sealed record EncryptedPayload : ProtocolModel
{
    /// <summary>
    /// The payload encryption algorithm identifier, which must be <c>AES-256-GCM</c>.
    /// </summary>
    public required string Alg { get; init; }
    /// <summary>
    /// The 12-byte nonce used by the payload's authenticated-encryption algorithm.
    /// </summary>
    public required ImmutableArray<byte> Nonce { get; init; }
    /// <summary>
    /// The encrypted content, including its authentication tag.
    /// </summary>
    public required ImmutableArray<byte> Ciphertext { get; init; }

    /// <summary>
    /// Validates the AES-256-GCM algorithm, nonce length, and ciphertext structure.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Alg != "AES-256-GCM")
            return new(ProtocolViolationKind.Unsupported, "Message encryption must use AES-256-GCM.");
        return Nonce.AsSpan().Length != 12 || Ciphertext.AsSpan().Length <= 16
            ? new(ProtocolViolationKind.Format, "The encrypted payload requires a 12-byte nonce, encrypted content and a 16-byte authentication tag.")
            : null;
    }
}
