using Meshline.Identity;
using Meshline.Validation;
using System.Collections.Immutable;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains the group-specific private encryption key synchronized between an account's devices.
/// </summary>
public sealed record GroupMemberPrivateState : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The relay's lowercase Neo script hash, including the <c>0x</c> prefix.
    /// </summary>
    public required string RelayId { get; init; }
    /// <summary>
    /// The account's 32-byte group-specific X25519 private key; treat it as secret material.
    /// </summary>
    public required ImmutableArray<byte> MemberEncryptionPrivateKey { get; init; }

    /// <summary>
    /// Validates the group and relay identifiers and private member-key length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        if (RelayIdentity.ValidateRelayId(RelayId) is { } relayViolation)
            return relayViolation;
        return MemberEncryptionPrivateKey.AsSpan().Length != 32
            ? new(ProtocolViolationKind.Format, "The member encryption private key must contain 32 bytes.")
            : null;
    }

    /// <summary>
    /// Appends the record's diagnostic fields while omitting sensitive material from its string representation.
    /// </summary>
    /// <param name="builder">The builder to which diagnostic fields are appended.</param>
    /// <returns><see langword="true"/> after appending the record's diagnostic fields.</returns>
    protected override bool PrintMembers(StringBuilder builder)
    {
        if (base.PrintMembers(builder))
            builder.Append(", ");

        builder.Append($"{nameof(GroupId)} = {GroupId}, {nameof(RelayId)} = {RelayId}");
        return true;
    }
}
