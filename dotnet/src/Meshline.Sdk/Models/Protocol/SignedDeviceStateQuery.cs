using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Requests account device state using signed device identity and authorization evidence.
/// </summary>
public sealed record SignedDeviceStateQuery() : TypedProtocolModel("meshline.device.state.resolve")
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The contact grant or invitation supplied as authorization evidence.
    /// </summary>
    public required TypedProtocolModel Authorization { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }
    /// <summary>
    /// The creation time, in Unix seconds.
    /// </summary>
    public required long CreatedAt { get; init; }
    /// <summary>
    /// The device signature over the model's device signing input.
    /// </summary>
    public required ImmutableArray<byte> DeviceSignature { get; init; }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(NetworkContext context) =>
        GetSigningInput(context, "device_signature");
}
