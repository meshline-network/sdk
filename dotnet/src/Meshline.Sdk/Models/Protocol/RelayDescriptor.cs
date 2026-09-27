using Meshline.Identity;
using Meshline.Validation;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a relay's signed identity, endpoints, capabilities, and expiration time.
/// </summary>
public sealed record RelayDescriptor() : TypedProtocolModel("meshline.relay.descriptor")
{
    /// <summary>
    /// The relay's lowercase Neo script hash, including the <c>0x</c> prefix.
    /// </summary>
    public required string RelayId { get; init; }
    /// <summary>
    /// The relay's Neo signing public key.
    /// </summary>
    public required ImmutableArray<byte> PublicKey { get; init; }
    /// <summary>
    /// The relay's advertised HTTPS, optional WSS, and libp2p TCP endpoints.
    /// </summary>
    public required ImmutableArray<string> Endpoints { get; init; }
    /// <summary>
    /// The protocol capability identifiers advertised by the relay.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<string> Capabilities { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }
    /// <summary>
    /// The relay signature over the model's relay signing input.
    /// </summary>
    public required ImmutableArray<byte> RelaySignature { get; init; }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(NetworkContext context) =>
        GetSigningInput(context, "relay_signature");

    /// <summary>
    /// Validates relay identity, endpoints, capabilities, expiry, and the relay signature.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform account or relay signature verification; ordinary invalid signatures are returned as protocol violations.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (PublicKey.AsSpan().Length != 33)
            return new(ProtocolViolationKind.Format, "The relay public key must contain 33 compressed P-256 bytes.");
        if (RelaySignature.AsSpan().Length != 64)
            return new(ProtocolViolationKind.Format, "The relay signature must contain 64 bytes.");
        if (ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
            return new(ProtocolViolationKind.Time, "The relay descriptor has expired.");
        if (RelayIdentity.ValidateRelayId(RelayId) is { } relayViolation)
            return relayViolation;
        if (Endpoints.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "Relay endpoints must be a nonempty array.");

        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var hasHttps = false;
        string? peerId = null;
        foreach (var endpoint in Endpoints)
        {
            if (!addresses.Add(endpoint))
                return new(ProtocolViolationKind.Format, "Relay endpoints cannot contain duplicates.");
            if (RelayEndpointValidator.Validate(endpoint, out var kind, out var candidatePeerId) is { } endpointViolation)
                return endpointViolation;

            if (kind == RelayEndpointKind.Https)
                hasHttps = true;
            else if (kind == RelayEndpointKind.Libp2pTcp)
            {
                if (peerId is not null && peerId != candidatePeerId)
                    return new(ProtocolViolationKind.Identity, "All libp2p TCP endpoints must identify the same peer.");
                peerId = candidatePeerId;
            }
        }

        if (!hasHttps || peerId is null)
            return new(ProtocolViolationKind.Format, "The relay must provide an HTTPS endpoint and a libp2p TCP endpoint.");

        if (!Capabilities.IsDefault && Capabilities.Length > 64)
            return new(ProtocolViolationKind.Format, "Relay capabilities cannot contain more than 64 items.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in Capabilities.IsDefault ? ImmutableArray<string>.Empty : Capabilities)
        {
            if (string.IsNullOrEmpty(capability) || Encoding.UTF8.GetByteCount(capability) > 128)
                return new(ProtocolViolationKind.Format, "Each capability must be nonempty and cannot exceed 128 UTF-8 bytes.");
            if (!names.Add(capability))
                return new(ProtocolViolationKind.Format, "Relay capabilities cannot contain duplicates.");
        }

        try
        {
            if (RelayIdentity.GetRelayId(PublicKey.AsSpan()) != RelayId)
                return new(ProtocolViolationKind.Identity, "The public key does not identify the relay.");
            return RelayIdentity.VerifySignature(PublicKey.AsSpan(), GetSigningInput(context), RelaySignature.AsSpan())
                ? null
                : new(ProtocolViolationKind.Signature, "The relay descriptor signature is invalid.");
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
        catch (FormatException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
    }
}
