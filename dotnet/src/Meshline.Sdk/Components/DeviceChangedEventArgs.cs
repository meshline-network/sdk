namespace Meshline.Components;

/// <summary>
/// Identifies a device whose local certificate changed.
/// </summary>
public sealed class DeviceChangedEventArgs : EventArgs
{
    /// <summary>
    /// The device's canonical <c>dev_</c> identifier.
    /// </summary>
    public string DeviceId { get; }

    internal DeviceChangedEventArgs(string deviceId)
    {
        DeviceId = deviceId;
    }
}
