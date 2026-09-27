using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a device-signed, expiring invitation to join a group.
/// </summary>
public sealed record GroupInvite() : TypedProtocolModel("meshline.group.invite")
{
    /// <summary>
    /// The invitation's canonical <c>inv_</c> identifier.
    /// </summary>
    public required string InviteId { get; init; }
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The CAIP-10 account identifier of the invitation's creator.
    /// </summary>
    public required string Inviter { get; init; }
    /// <summary>
    /// The account allowed to use a targeted invitation, or <see langword="null"/> for a shareable invitation.
    /// </summary>
    public string? Invitee { get; init; }
    /// <summary>
    /// The maximum number of invitation uses, or <see langword="null"/> when no explicit use limit is supplied.
    /// </summary>
    public long? MaxUses { get; init; }
    /// <summary>
    /// The creation time, in Unix seconds.
    /// </summary>
    public required long CreatedAt { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }
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
    /// Validates invitation identifiers, target and use-limit rules, validity times, and signature length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateInviteId(InviteId) is { } inviteViolation)
            return inviteViolation;
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        if (MaxUses is <= 0 || Invitee is not null && MaxUses is not null)
            return new(ProtocolViolationKind.Format, "Only shareable invitations may specify a positive use limit.");
        if (CreatedAt < 0 || ExpiresAt <= CreatedAt)
            return new(ProtocolViolationKind.Time, "The invitation must have a nonnegative creation time and expire after its creation.");
        if (ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
            return new(ProtocolViolationKind.Time, "The group invitation has expired.");
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The group invitation signature must contain 64 bytes.");

        try
        {
            if (AccountAdapter.ValidateAccountId(Inviter) is { } inviterViolation)
                return inviterViolation;

            return Invitee is { } invitee ? AccountAdapter.ValidateAccountId(invitee) : null;
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
