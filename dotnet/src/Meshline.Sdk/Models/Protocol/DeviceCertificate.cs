using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Binds device signing and encryption keys to an account for a fixed validity period.
/// </summary>
public sealed record DeviceCertificate() : TypedProtocolModel("meshline.device.certificate")
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The public key used to verify the account's signature and identifier.
    /// </summary>
    public required ImmutableArray<byte> AccountPublicKey { get; init; }
    /// <summary>
    /// The device's 32-byte Ed25519 signing public key.
    /// </summary>
    public required ImmutableArray<byte> SigningPublicKey { get; init; }
    /// <summary>
    /// The device's 32-byte X25519 encryption public key.
    /// </summary>
    public required ImmutableArray<byte> EncryptionPublicKey { get; init; }
    /// <summary>
    /// The inclusive start of the certificate validity window, in Unix seconds.
    /// </summary>
    public required long NotBefore { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }
    /// <summary>
    /// The device signature over the model's device signing input.
    /// </summary>
    public required ImmutableArray<byte> DeviceSignature { get; init; }
    /// <summary>
    /// The account signature over the model's account signing input.
    /// </summary>
    public required ImmutableArray<byte> AccountSignature { get; init; }

    /// <summary>
    /// Derives the device identifier from the account, device public keys, and network context.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical network-bound device identifier.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public string GetDeviceId(NetworkContext context) =>
        Identifiers.DeriveDeviceId(this, context);

    /// <summary>
    /// Validates the certificate's keys, identity, validity-window structure, size, and both device and account signatures.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// This checks both signatures and the certificate validity-window structure. It does not require the current time to lie within that window; check account device authorization separately.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform account or relay signature verification; ordinary invalid signatures are returned as protocol violations.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (SigningPublicKey.AsSpan().Length != Ed25519.PublicKeySize || EncryptionPublicKey.AsSpan().Length != 32)
            return new(ProtocolViolationKind.Format, "Device public keys must contain 32 bytes.");
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The device signature must contain 64 bytes.");
        if (AccountPublicKey.IsDefaultOrEmpty || AccountSignature.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "The account public key and signature must not be empty.");
        if (NotBefore < 0)
            return new(ProtocolViolationKind.Time, "The certificate start time must be nonnegative.");
        if (ExpiresAt <= NotBefore || (Int128)ExpiresAt - NotBefore > 720L * 24 * 60 * 60)
            return new(ProtocolViolationKind.Time, "The certificate validity period must be positive and cannot exceed 720 days.");

        try
        {
            if (AccountAdapter.ValidateAccountId(Account) is { } violation)
                return violation;

            if (!AccountAdapter.MatchesPublicKey(Account, AccountPublicKey.AsSpan()))
                return new(ProtocolViolationKind.Identity, "The account public key does not identify the certificate account.");
            if (Encoding.UTF8.GetByteCount(ToJson()) > 4096)
                return new(ProtocolViolationKind.Format, "The certificate cannot exceed 4096 canonical JSON bytes.");

            if (!Ed25519.Verify(DeviceSignature.AsSpan(), SigningPublicKey.AsSpan(), GetDeviceSigningInput(context)))
                return new(ProtocolViolationKind.Signature, "The device signature is invalid.");
            if (!AccountAdapter.For(Account).VerifySignature(AccountPublicKey.AsSpan(), GetAccountSigningInput(context), AccountSignature.AsSpan()))
                return new(ProtocolViolationKind.Signature, "The account signature is invalid.");

            return null;
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
        catch (FormatException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetDeviceSigningInput(NetworkContext context) =>
        GetSigningInput(context, "device_signature", "account_signature");

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetAccountSigningInput(NetworkContext context) =>
        GetSigningInput(context, "account_signature");
}
