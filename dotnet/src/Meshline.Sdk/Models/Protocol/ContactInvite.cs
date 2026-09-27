using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains an expiring, device-signed invitation to contact an account.
/// </summary>
public sealed record ContactInvite() : TypedProtocolModel("meshline.contact.invite")
{
    /// <summary>
    /// The CAIP-10 account identifier of the invitation's creator.
    /// </summary>
    public required string Inviter { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }
    /// <summary>
    /// The identifier of the device that signed the associated payload.
    /// </summary>
    public required string SignerDeviceId { get; init; }
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
    /// Validates inviter and signer identifiers, expiry, and signature length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The invitation signature must contain 64 bytes.");
        if (ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
            return new(ProtocolViolationKind.Time, "The contact invitation has expired.");
        if (Identifiers.ValidateDeviceId(SignerDeviceId) is { } deviceViolation)
            return deviceViolation;

        try
        {
            return AccountAdapter.ValidateAccountId(Inviter);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
