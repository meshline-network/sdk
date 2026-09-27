using Meshline.Validation;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Meshline.Models.Protocol;

/// <summary>
/// Identifies external content by location, plaintext digest, media type, and size.
/// </summary>
public sealed record ContentReference : ProtocolModel
{
    /// <summary>
    /// The URI from which the attachment content can be retrieved.
    /// </summary>
    public required string Uri { get; init; }
    /// <summary>
    /// The plaintext SHA-256 digest in canonical <c>sha256:</c> base64url form.
    /// </summary>
    public required string Hash { get; init; }
    /// <summary>
    /// The media type describing the content.
    /// </summary>
    public required string ContentType { get; init; }
    /// <summary>
    /// The size of the plaintext attachment in bytes.
    /// </summary>
    public required long Size { get; init; }
    /// <summary>
    /// The attachment encryption parameters, or <see langword="null"/> for unencrypted content.
    /// </summary>
    public ContentEncryption? Encryption { get; init; }

    /// <summary>
    /// The corresponding <c>ni:///sha-256;</c> URI used to reference this attachment in message text.
    /// </summary>
    [JsonIgnore]
    public string HashUri => $"ni:///sha-256;{Hash[7..]}";

    /// <summary>
    /// Resolves a hash-based attachment URI against the supplied content references.
    /// </summary>
    /// <param name="target">The <c>ni:///sha-256;</c> attachment reference to resolve.</param>
    /// <param name="attachments">The attachment references to search by plaintext digest.</param>
    /// <returns>The first matching attachment, or <see langword="null"/> when the URI is unsupported or no digest matches.</returns>
    /// <exception cref="ArgumentNullException">The <paramref name="attachments"/> collection is null when a valid content hash is resolved.</exception>
    public static ContentReference? Resolve(string target, IReadOnlyList<ContentReference> attachments)
    {
        var value = target.AsSpan();
        if (!value.StartsWith("ni:///", StringComparison.OrdinalIgnoreCase) || value[6..].IndexOfAny('/', '?', '#') >= 0)
            return null;

        value = value[6..];
        var separator = value.IndexOf(';');
        if (separator < 0 || System.Uri.UnescapeDataString(value[..separator].ToString()) != "sha-256")
            return null;

        var hash = "sha256:" + System.Uri.UnescapeDataString(value[(separator + 1)..].ToString());
        if (!IsValidHash(hash))
            return null;

        return attachments.FirstOrDefault(p => p.Hash == hash);
    }

    /// <summary>
    /// Validates the HTTPS location, plaintext hash, media type, size, and optional AES-256-GCM parameters.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) || uri.Scheme != System.Uri.UriSchemeHttps
            || !uri.IsWellFormedOriginalString() || uri.HostNameType == UriHostNameType.Unknown
            || Uri.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
            return new(ProtocolViolationKind.Format, "Content must have an absolute HTTPS URI with a valid host.");
        if (!IsValidHash(Hash))
            return new(ProtocolViolationKind.Format, "Expected a sha256: hash containing 32 canonical base64url-encoded bytes.");
        if (!MediaTypeValidator.IsValid(ContentType))
            return new(ProtocolViolationKind.Format, "Content must have a valid media type.");
        if (Size < 0)
            return new(ProtocolViolationKind.Format, "Content size must be nonnegative.");

        return Encryption?.Validate(context);
    }

    /// <summary>
    /// Checks the plaintext attachment's length and SHA-256 digest against this reference.
    /// </summary>
    /// <param name="plaintext">The plaintext content bytes.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <exception cref="FormatException">The stored digest is not valid base64url; validate the content reference before verifying bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The stored hash is too short to contain its expected prefix and digest.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot compute the plaintext SHA-256 digest.</exception>
    public ProtocolViolation? Verify(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length != Size)
            return new(ProtocolViolationKind.Format, "The plaintext length does not match the content size.");

        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(plaintext), Base64Url.DecodeFromChars(Hash.AsSpan(7)))
            ? null
            : new(ProtocolViolationKind.Identity, "The plaintext SHA-256 digest does not match the content hash.");
    }

    static bool IsValidHash(string hash)
    {
        var value = hash.AsSpan();
        return value.Length == 50 && value.StartsWith("sha256:", StringComparison.Ordinal) && Base64UrlValidator.IsValid(value[7..]);
    }
}
