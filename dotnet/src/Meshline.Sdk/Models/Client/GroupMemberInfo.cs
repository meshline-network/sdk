using Meshline.Models.Protocol;
using System.Collections.Immutable;

namespace Meshline.Models.Client;

/// <summary>
/// Contains a locally known group member's role, encryption key, and nickname.
/// </summary>
public sealed class GroupMemberInfo
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string AccountId { get; init; }
    /// <summary>
    /// The account's group role.
    /// </summary>
    public required GroupRole Role { get; init; }
    /// <summary>
    /// The account's 32-byte group-specific X25519 public key.
    /// </summary>
    public required ImmutableArray<byte> MemberEncryptionPublicKey { get; init; }
    /// <summary>
    /// The optional display nickname.
    /// </summary>
    public string? Nickname { get; init; }
}
