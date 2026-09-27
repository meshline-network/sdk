namespace Meshline.Models.Protocol;

/// <summary>
/// Announces a new account device-state revision through relay notifications.
/// </summary>
public sealed record DeviceStateChangedNotification : ProtocolModel
{
    /// <summary>
    /// The monotonically increasing revision of the document.
    /// </summary>
    public required long Revision { get; init; }
}
