using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a message key encrypted to one device using HPKE.
/// </summary>
public sealed record MessageKeyBox : ProtocolModel
{
    /// <summary>
    /// The device's canonical <c>dev_</c> identifier.
    /// </summary>
    public required string DeviceId { get; init; }
    /// <summary>
    /// The HPKE suite identifier <c>X25519-HKDF-SHA256-AES256GCM</c>.
    /// </summary>
    public required string Alg { get; init; }
    /// <summary>
    /// The 32-byte HPKE encapsulated X25519 public key.
    /// </summary>
    public required ImmutableArray<byte> Enc { get; init; }
    /// <summary>
    /// The message key encrypted with HPKE for the target device.
    /// </summary>
    public required ImmutableArray<byte> SealedKey { get; init; }

    /// <summary>
    /// Validates the device identifier, HPKE suite, encapsulated key, and encrypted message-key size.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateDeviceId(DeviceId) is { } deviceViolation)
            return deviceViolation;
        if (Alg != "X25519-HKDF-SHA256-AES256GCM")
            return new(ProtocolViolationKind.Unsupported, "Message key boxes must use X25519-HKDF-SHA256-AES256GCM.");
        return Enc.AsSpan().Length != 32 || SealedKey.AsSpan().Length != 60
            ? new(ProtocolViolationKind.Format, "A message key box requires a 32-byte ephemeral public key and a 60-byte sealed key.")
            : null;
    }
}
