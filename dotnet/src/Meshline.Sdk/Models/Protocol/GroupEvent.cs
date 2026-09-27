namespace Meshline.Models.Protocol;

/// <summary>
/// Wraps a group timeline payload with its sequence, key epoch, and acceptance information.
/// </summary>
public sealed record GroupEvent : ProtocolModel
{
    /// <summary>
    /// The timeline sequence assigned by the hosting relay.
    /// </summary>
    public required long Sequence { get; init; }
    /// <summary>
    /// The group key epoch associated with this record.
    /// </summary>
    public required long Epoch { get; init; }
    /// <summary>
    /// The typed message or management payload carried by the event.
    /// </summary>
    public required TypedProtocolModel Payload { get; init; }
    /// <summary>
    /// The relay acceptance time, in Unix seconds.
    /// </summary>
    public required long AcceptedAt { get; init; }
    /// <summary>
    /// The payload signer's device identifier, or <see langword="null"/> for a relay-generated key-rotation event.
    /// </summary>
    public string? SignerDeviceId { get; init; }
}
