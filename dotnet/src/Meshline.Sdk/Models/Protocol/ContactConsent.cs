using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Carries device authorization evidence and a contact grant, with an optional request note.
/// </summary>
public sealed record ContactConsent() : TypedProtocolModel("meshline.contact.consent")
{
    /// <summary>
    /// The grantor's signed device state supplied as evidence for validating the contact grant.
    /// </summary>
    public required AccountDeviceState DeviceState { get; init; }
    /// <summary>
    /// The contact grant issued to the recipient of the consent document.
    /// </summary>
    public required ContactGrant Grant { get; init; }
    /// <summary>
    /// The optional note accompanying the contact request.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// Validates the enclosed device state and grant, checks the optional note, and verifies a grant signature from a currently authorized device.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform account or relay signature verification; ordinary invalid signatures are returned as protocol violations.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Note is not null && (string.IsNullOrWhiteSpace(Note) || Encoding.UTF8.GetByteCount(Note) > 1024))
            return new(ProtocolViolationKind.Format, "The consent note must contain non-whitespace text and cannot exceed 1024 UTF-8 bytes.");
        if (DeviceState.Account != Grant.Grantor)
            return new(ProtocolViolationKind.Identity, "The consent device state must belong to the grantor.");

        try
        {
            if (DeviceState.Validate(context) is { } stateViolation)
                return stateViolation;
            if (Grant.Validate(context) is { } grantViolation)
                return grantViolation;

            var input = Grant.GetSigningInput(context);
            var timestamp = Clock.UtcNow.ToUnixTimeSeconds();
            var hasCurrentSigner = false;
            foreach (var certificate in DeviceState.Certificates)
            {
                if (certificate.NotBefore > timestamp || certificate.ExpiresAt <= timestamp
                    || !Grant.Signatures.TryGetValue(certificate.GetDeviceId(context), out var signature))
                    continue;

                hasCurrentSigner = true;
                if (Ed25519.Verify(signature.AsSpan(), certificate.SigningPublicKey.AsSpan(), input))
                    return null;
            }

            return hasCurrentSigner
                ? new(ProtocolViolationKind.Signature, "No current device has a valid contact grant signature.")
                : new(ProtocolViolationKind.Authorization, "The contact grant has no signature from a currently authorized device.");
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
    }
}
