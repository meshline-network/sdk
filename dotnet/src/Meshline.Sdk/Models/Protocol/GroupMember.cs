using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a group member's account identifier, role, and encryption public key.
/// </summary>
public sealed record GroupMember : ProtocolModel
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The account's group role.
    /// </summary>
    public required GroupRole Role { get; init; }
    /// <summary>
    /// The account's 32-byte group-specific X25519 public key.
    /// </summary>
    public required ImmutableArray<byte> MemberEncryptionPublicKey { get; init; }
}
