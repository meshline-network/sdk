using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a page of account messages and the certificates needed to verify them.
/// </summary>
public sealed record MessageTimelinePage : ProtocolModel
{
    /// <summary>
    /// The result entries in this page.
    /// </summary>
    public required ImmutableArray<MessageTimelineEntry> Items { get; init; }
    /// <summary>
    /// The device certificates carried by this document or page.
    /// </summary>
    public required ImmutableArray<DeviceCertificate> Certificates { get; init; }
    /// <summary>
    /// Whether additional entries remain beyond this page.
    /// </summary>
    public required bool HasMore { get; init; }
    /// <summary>
    /// Whether retained history omits entries needed to span the requested synchronization range.
    /// </summary>
    public bool? HasRetentionGap { get; init; }
}
