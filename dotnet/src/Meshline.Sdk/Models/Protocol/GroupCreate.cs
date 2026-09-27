using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a signed group creation document and the initial owner's encryption identity.
/// </summary>
public sealed record GroupCreate() : TypedProtocolModel("meshline.group.create")
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The 16-byte creation nonce from which the group identifier is derived.
    /// </summary>
    public required ImmutableArray<byte> Nonce { get; init; }
    /// <summary>
    /// The display name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// The optional human-readable description.
    /// </summary>
    public string? Description { get; init; }
    /// <summary>
    /// The maximum permitted number of group members.
    /// </summary>
    public required long MemberCapacity { get; init; }
    /// <summary>
    /// The policy controlling which members can create invitations.
    /// </summary>
    public required GroupInvitePolicy InvitePolicy { get; init; }
    /// <summary>
    /// The initial owner's account identifier and group-specific encryption public key.
    /// </summary>
    public required GroupMemberKey Owner { get; init; }
    /// <summary>
    /// The commitment identifying the group client secret.
    /// </summary>
    public required string ClientSecretCommitment { get; init; }
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
    /// Validates creation metadata, nonce, capacity, owner key, client-secret commitment, and signature length.
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
        if (Nonce.AsSpan().Length != 16)
            return new(ProtocolViolationKind.Format, "The group nonce must contain 16 bytes.");
        if (string.IsNullOrWhiteSpace(Name) || Encoding.UTF8.GetByteCount(Name) > 256)
            return new(ProtocolViolationKind.Format, "The group name must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.");
        if (Description is { Length: > 0 } description && (string.IsNullOrWhiteSpace(description) || Encoding.UTF8.GetByteCount(description) > 4096))
            return new(ProtocolViolationKind.Format, "A nonempty group description must contain non-whitespace text and cannot exceed 4096 UTF-8 bytes.");
        if (MemberCapacity <= 0)
            return new(ProtocolViolationKind.Format, "The group member capacity must be positive.");
        var commitment = ClientSecretCommitment.AsSpan();
        if (commitment.Length != 50 || !commitment.StartsWith("sha256:", StringComparison.Ordinal) || !Base64UrlValidator.IsValid(commitment[7..]))
            return new(ProtocolViolationKind.Format, "The client secret commitment must contain 32 canonical base64url-encoded bytes after sha256:.");
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The group creation signature must contain 64 bytes.");
        return Owner.Validate(context);
    }
}
