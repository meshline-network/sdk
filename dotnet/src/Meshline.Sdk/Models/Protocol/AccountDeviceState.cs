using Meshline.Identity;
using Meshline.Validation;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains the complete account-signed list of authorized device certificates at one revision.
/// </summary>
public sealed record AccountDeviceState() : TypedProtocolModel("meshline.account.device.state")
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
    /// The monotonically increasing revision of the document.
    /// </summary>
    public required long Revision { get; init; }
    /// <summary>
    /// The device certificates carried by this document or page.
    /// </summary>
    public required ImmutableArray<DeviceCertificate> Certificates { get; init; }
    /// <summary>
    /// The account signature over the model's account signing input.
    /// </summary>
    public required ImmutableArray<byte> AccountSignature { get; init; }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(NetworkContext context) =>
        GetSigningInput(context, "account_signature");

    /// <summary>
    /// Selects a nonnegative safe-integer revision strictly greater than the known revision.
    /// </summary>
    /// <param name="requested">The explicitly requested revision, or <see langword="null"/> to increment the known revision.</param>
    /// <param name="known">The highest known revision; use <c>-1</c> when no revision is known.</param>
    /// <returns>The requested revision, or the known revision plus one when no revision was supplied.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The selected revision is negative, exceeds 2^53 - 1, or is not greater than the known revision.</exception>
    public static long GetNextRevision(long? requested, long known)
    {
        const long maximum = 9_007_199_254_740_991;
        var next = requested ?? known + 1;
        if (next < 0 || next > maximum || next <= known)
            throw new ArgumentOutOfRangeException(nameof(requested), "A new revision must be a nonnegative safe integer greater than every known revision.");
        return next;
    }

    /// <summary>
    /// Validates the complete device set, certificate identities and signatures, and the enclosing account signature.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform account or relay signature verification; ordinary invalid signatures are returned as protocol violations.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Revision < 0)
            return new(ProtocolViolationKind.Format, "The device state revision must be nonnegative.");
        if (Certificates.IsDefault || Certificates.Length > 8)
            return new(ProtocolViolationKind.Format, "The device state must contain an initialized array of at most 8 certificates.");
        if (AccountPublicKey.IsDefaultOrEmpty || AccountSignature.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "The account public key and signature must not be empty.");

        try
        {
            if (AccountAdapter.ValidateAccountId(Account) is { } violation)
                return violation;

            if (!AccountAdapter.MatchesPublicKey(Account, AccountPublicKey.AsSpan()))
                return new(ProtocolViolationKind.Identity, "The account public key does not identify the device state account.");
            if (Encoding.UTF8.GetByteCount(ToJson()) > 131072)
                return new(ProtocolViolationKind.Format, "The device state cannot exceed 131072 canonical JSON bytes.");

            var deviceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var certificate in Certificates)
            {
                if (certificate is null)
                    return new(ProtocolViolationKind.Format, "The device state cannot contain a null certificate.");
                if (certificate.Account != Account)
                    return new(ProtocolViolationKind.Identity, "Every certificate must belong to the device state account.");
                if (certificate.Validate(context) is { } certificateViolation)
                    return certificateViolation;
                if (!deviceIds.Add(certificate.GetDeviceId(context)))
                    return new(ProtocolViolationKind.Conflict, "The device state cannot contain duplicate device identities.");
            }

            if (!AccountAdapter.For(Account).VerifySignature(AccountPublicKey.AsSpan(), GetSigningInput(context), AccountSignature.AsSpan()))
                return new(ProtocolViolationKind.Signature, "The device state account signature is invalid.");

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
    /// Checks that a device is registered in this state and its certificate is currently within its validity window.
    /// </summary>
    /// <param name="deviceId">The canonical device identifier.</param>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// This method checks registration and the current validity window. Validate the enclosing signed device state separately before using it as authorization evidence.
    /// </remarks>
    /// <exception cref="JsonException">A certificate cannot be serialized to compute the device identifier.</exception>
    public ProtocolViolation? ValidateDeviceAuthorization(string deviceId, NetworkContext context)
    {
        if (Identifiers.ValidateDeviceId(deviceId) is { } identifierViolation)
            return identifierViolation;

        var certificate = Certificates.FirstOrDefault(certificate => certificate.GetDeviceId(context) == deviceId);
        if (certificate is null)
            return new(ProtocolViolationKind.Authorization, "The device is not registered in this account device state.");

        var now = Clock.UtcNow.ToUnixTimeSeconds();
        return certificate.NotBefore <= now && now < certificate.ExpiresAt
            ? null
            : new(ProtocolViolationKind.Time, "The registered device certificate is not currently valid.");
    }
}
