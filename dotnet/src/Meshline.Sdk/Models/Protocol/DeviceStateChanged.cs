namespace Meshline.Models.Protocol;

/// <summary>
/// Announces a new account device-state revision inside an account message.
/// </summary>
public sealed record DeviceStateChanged() : TypedProtocolModel("meshline.device.state.changed")
{
    /// <summary>
    /// The monotonically increasing revision of the document.
    /// </summary>
    public required long Revision { get; init; }
}
