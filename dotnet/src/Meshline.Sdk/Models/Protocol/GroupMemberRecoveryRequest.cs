using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Requests recovery of group access using a replacement member encryption public key.
/// </summary>
public sealed record GroupMemberRecoveryRequest() : TypedProtocolModel("meshline.group.member.recovery")
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The account's 32-byte group-specific X25519 public key.
    /// </summary>
    public required ImmutableArray<byte> MemberEncryptionPublicKey { get; init; }
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
    /// Validates the account and group identifiers, replacement public key, and signature length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        if (MemberEncryptionPublicKey.AsSpan().Length != X25519.PointSize)
            return new(ProtocolViolationKind.Format, "The member encryption public key must contain 32 bytes.");
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The member recovery signature must contain 64 bytes.");

        try
        {
            return AccountAdapter.ValidateAccountId(Account);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
