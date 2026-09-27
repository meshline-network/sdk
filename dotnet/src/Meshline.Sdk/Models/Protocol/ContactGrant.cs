using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Authorizes a grantee to contact a grantor using signatures from the grantor's devices.
/// </summary>
public sealed record ContactGrant() : TypedProtocolModel("meshline.contact.grant")
{
    /// <summary>
    /// The account granting permission to contact it.
    /// </summary>
    public required string Grantor { get; init; }
    /// <summary>
    /// The account receiving permission to contact the grantor.
    /// </summary>
    public required string Grantee { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds, or <see langword="null"/> when unavailable.
    /// </summary>
    public long? ExpiresAt { get; init; }
    /// <summary>
    /// Device signatures keyed by the grantor's device identifiers, using ordinal key comparison.
    /// </summary>
    public required ImmutableDictionary<string, ImmutableArray<byte>> Signatures { get; init => field = value.WithComparers(StringComparer.Ordinal); }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(NetworkContext context) =>
        GetSigningInput(context, "signatures");

    /// <summary>
    /// Validates grant identities, expiry, and the format of the device-signature map.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Signatures.IsEmpty)
            return new(ProtocolViolationKind.Format, "The contact grant must contain at least one device signature.");
        if (ExpiresAt is { } expiresAt && expiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
            return new(ProtocolViolationKind.Time, "The contact grant has expired.");

        foreach (var (deviceId, signature) in Signatures)
        {
            if (Identifiers.ValidateDeviceId(deviceId) is { } deviceViolation)
                return deviceViolation;
            if (signature.AsSpan().Length != Ed25519.SignatureSize)
                return new(ProtocolViolationKind.Format, "Each contact grant signature must contain 64 bytes.");
        }

        try
        {
            if (AccountAdapter.ValidateAccountId(Grantor) is { } grantorViolation)
                return grantorViolation;
            return AccountAdapter.ValidateAccountId(Grantee);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
