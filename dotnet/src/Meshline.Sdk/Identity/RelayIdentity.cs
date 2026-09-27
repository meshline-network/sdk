using Meshline.Validation;
using System.Buffers;

namespace Meshline.Identity;

/// <summary>
/// Derives Neo relay identifiers and validates relay identities and signatures.
/// </summary>
public static class RelayIdentity
{
    static readonly SearchValues<char> s_hexChars = SearchValues.Create("0123456789abcdef");

    /// <summary>
    /// Derives the relay's lowercase Neo script-hash identifier from its public key.
    /// </summary>
    /// <param name="publicKey">The public key in the encoding required by the identity's signature algorithm.</param>
    /// <returns>A lowercase <c>0x</c>-prefixed 160-bit relay script hash.</returns>
    /// <exception cref="FormatException">The public key is not a valid 33-byte compressed Neo N3 P-256 key.</exception>
    public static string GetRelayId(ReadOnlySpan<byte> publicKey)
    {
        var hash = NeoAccountAdapter.GetScriptHash(publicKey);
        Array.Reverse(hash);
        return "0x" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Checks that a relay identifier is a canonical lowercase Neo script hash.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public static ProtocolViolation? ValidateRelayId(string relayId)
    {
        var value = relayId.AsSpan();
        return value.Length == 42 && value.StartsWith("0x", StringComparison.Ordinal)
            && !value[2..].ContainsAnyExcept(s_hexChars)
                ? null
                : new(ProtocolViolationKind.Format, "Expected a lowercase relay script hash.");
    }

    /// <summary>
    /// Verifies the signature using the public key and identity algorithm.
    /// </summary>
    /// <param name="publicKey">The public key in the encoding required by the identity's signature algorithm.</param>
    /// <param name="data">The exact bytes to sign or verify.</param>
    /// <param name="signature">The signature bytes to verify against the supplied input.</param>
    /// <returns><see langword="true"/> if the signature is valid; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The cryptographic provider cannot create or use the P-256 signature verifier; an invalid signature normally returns false.</exception>
    public static bool VerifySignature(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        return AccountAdapter.Neo.VerifySignature(publicKey, data, signature);
    }

    /// <summary>
    /// Verifies the signature and its binding to the supplied identity.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="publicKey">The public key in the encoding required by the identity's signature algorithm.</param>
    /// <param name="data">The exact bytes to sign or verify.</param>
    /// <param name="signature">The signature bytes to verify against the supplied input.</param>
    /// <returns><see langword="true"/> if the signature is valid and the public key matches the supplied identifier; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The cryptographic provider cannot create or use the P-256 signature verifier; an invalid signature normally returns false.</exception>
    public static bool VerifySignature(string relayId, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        return AccountAdapter.Neo.VerifySignature(publicKey, data, signature) && GetRelayId(publicKey) == relayId;
    }
}
