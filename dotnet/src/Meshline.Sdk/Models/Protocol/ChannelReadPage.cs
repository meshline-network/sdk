using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains channel timeline events and the certificates needed to verify their signers.
/// </summary>
public sealed record ChannelReadPage : ProtocolModel
{
    /// <summary>
    /// The timeline events in this page, in relay response order.
    /// </summary>
    public required ImmutableArray<ChannelEvent> Events { get; init; }
    /// <summary>
    /// The device certificates carried by this document or page.
    /// </summary>
    public required ImmutableArray<DeviceCertificate> Certificates { get; init; }
    /// <summary>
    /// Whether additional entries remain beyond this page.
    /// </summary>
    public required bool HasMore { get; init; }
}
