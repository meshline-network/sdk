using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc7748;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Associates an account with its group-specific encryption public key.
/// </summary>
public sealed record GroupMemberKey : ProtocolModel
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The account's 32-byte group-specific X25519 public key.
    /// </summary>
    public required ImmutableArray<byte> MemberEncryptionPublicKey { get; init; }

    /// <summary>
    /// Validates the account identifier and 32-byte member encryption public key.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (MemberEncryptionPublicKey.AsSpan().Length != X25519.PointSize)
            return new(ProtocolViolationKind.Format, "The member encryption public key must contain 32 bytes.");
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
