using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc7748;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Commits a new group client secret and optionally replaces the owner's member encryption key.
/// </summary>
public sealed record GroupSecretRotation() : GroupManagementOperation("meshline.group.secret.rotation")
{
    /// <summary>
    /// The commitment identifying the group client secret.
    /// </summary>
    public required string ClientSecretCommitment { get; init; }
    /// <summary>
    /// An optional replacement X25519 member public key for the group owner.
    /// </summary>
    public ImmutableArray<byte>? OwnerEncryptionPublicKey { get; init; }

    /// <summary>
    /// Validates management-chain fields, the client-secret commitment, and an optional replacement owner key.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (base.Validate(context) is { } violation)
            return violation;
        var commitment = ClientSecretCommitment.AsSpan();
        if (commitment.Length != 50 || !commitment.StartsWith("sha256:", StringComparison.Ordinal) || !Base64UrlValidator.IsValid(commitment[7..]))
            return new(ProtocolViolationKind.Format, "The client secret commitment must contain 32 canonical base64url-encoded bytes after sha256:.");
        return OwnerEncryptionPublicKey is { } key && key.AsSpan().Length != X25519.PointSize
            ? new(ProtocolViolationKind.Format, "The owner encryption public key must contain 32 bytes.")
            : null;
    }
}
