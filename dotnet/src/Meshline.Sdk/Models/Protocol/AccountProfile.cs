using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains an account's device-signed profile and public discovery preference.
/// </summary>
public sealed record AccountProfile() : TypedProtocolModel("meshline.profile")
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The optional display nickname.
    /// </summary>
    public string? Nickname { get; init; }
    /// <summary>
    /// The optional reference to the profile's avatar content.
    /// </summary>
    public ContentReference? Avatar { get; init; }
    /// <summary>
    /// The optional profile biography.
    /// </summary>
    public string? Bio { get; init; }
    /// <summary>
    /// Whether the account permits public profile discovery.
    /// </summary>
    public required bool PublicDiscovery { get; init; }
    /// <summary>
    /// The last update time, in Unix seconds.
    /// </summary>
    public required long UpdatedAt { get; init; }
    /// <summary>
    /// The device signature over the model's device signing input.
    /// </summary>
    public required ImmutableArray<byte> DeviceSignature { get; init; }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(NetworkContext context) =>
        GetSigningInput(context, "device_signature");

    /// <summary>
    /// Validates the profile account, text and avatar fields, timestamp, and signature length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Nickname is { Length: > 0 } nickname && (string.IsNullOrWhiteSpace(nickname) || Encoding.UTF8.GetByteCount(nickname) > 256))
            return new(ProtocolViolationKind.Format, "A nonempty nickname must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.");
        if (Bio is { Length: > 0 } bio && (string.IsNullOrWhiteSpace(bio) || Encoding.UTF8.GetByteCount(bio) > 2048))
            return new(ProtocolViolationKind.Format, "A nonempty bio must contain non-whitespace text and cannot exceed 2048 UTF-8 bytes.");
        if (UpdatedAt < 0)
            return new(ProtocolViolationKind.Time, "The profile update time must be nonnegative.");
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The profile signature must contain 64 bytes.");
        if (Avatar is { } avatar)
        {
            if (avatar.Validate(context) is { } avatarViolation)
                return avatarViolation;
            if (!avatar.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return new(ProtocolViolationKind.Format, "The profile avatar must have an image media type.");
        }

        try
        {
            if (AccountAdapter.ValidateAccountId(Account) is { } accountViolation)
                return accountViolation;

            return Encoding.UTF8.GetByteCount(ToJson()) <= 8192
                ? null
                : new(ProtocolViolationKind.Format, "The profile cannot exceed 8192 canonical JSON bytes.");
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
