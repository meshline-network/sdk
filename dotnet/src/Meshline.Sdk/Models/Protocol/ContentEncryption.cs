using Meshline.Validation;
using System.Collections.Immutable;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Describes the algorithm, key, and nonce used to encrypt an attachment.
/// </summary>
public sealed record ContentEncryption : ProtocolModel
{
    /// <summary>
    /// The attachment encryption algorithm identifier, which must be <c>AES-256-GCM</c>.
    /// </summary>
    public required string Alg { get; init; }
    /// <summary>
    /// The 32-byte AES-256-GCM attachment key; treat it as secret material.
    /// </summary>
    public required ImmutableArray<byte> Key { get; init; }
    /// <summary>
    /// The 12-byte AES-256-GCM nonce used to encrypt the attachment.
    /// </summary>
    public required ImmutableArray<byte> Nonce { get; init; }

    /// <summary>
    /// Validates the AES-256-GCM algorithm identifier, key length, and nonce length.
    /// </summary>
    /// <param name="context">The optional network context; these field checks do not depend on it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Alg != "AES-256-GCM")
            return new(ProtocolViolationKind.Unsupported, "Content encryption must use AES-256-GCM.");
        if (Key.AsSpan().Length != 32 || Nonce.AsSpan().Length != 12)
            return new(ProtocolViolationKind.Format, "Content encryption requires a 32-byte key and a 12-byte nonce.");

        return null;
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

        builder.Append($"{nameof(Alg)} = {Alg}, {nameof(Nonce)} = {Nonce}");
        return true;
    }
}
