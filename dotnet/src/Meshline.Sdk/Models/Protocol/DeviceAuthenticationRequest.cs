using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Carries a device's signed response to a relay authentication challenge.
/// </summary>
public sealed record DeviceAuthenticationRequest : ProtocolModel
{
    /// <summary>
    /// The relay-issued challenge nonce being signed.
    /// </summary>
    public required string Nonce { get; init; }
    /// <summary>
    /// The message or request timestamp, in Unix seconds.
    /// </summary>
    public required long Timestamp { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }
    /// <summary>
    /// The device signature over the model's device signing input.
    /// </summary>
    public required ImmutableArray<byte> DeviceSignature { get; init; }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="origin">The relay endpoint origin bound into the authentication signature.</param>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(string relayId, string origin, NetworkContext context) =>
        new DeviceAuthentication
        {
            RelayId = relayId,
            Origin = origin,
            Account = SignerCertificate.Account,
            DeviceId = SignerCertificate.GetDeviceId(context),
            Nonce = Nonce,
            Timestamp = Timestamp
        }.GetSigningInput(context);
}

file sealed record DeviceAuthentication() : TypedProtocolModel("meshline.relay.auth")
{
    public required string RelayId { get; init; }
    public required string Origin { get; init; }
    public required string Account { get; init; }
    public required string DeviceId { get; init; }
    public required string Nonce { get; init; }
    public required long Timestamp { get; init; }
}
