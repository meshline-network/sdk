namespace Meshline.Models.Client;

/// <summary>
/// Supplies certificate settings for creating a new local device.
/// </summary>
public sealed class DeviceCreationOptions
{
    /// <summary>
    /// The certificate lifetime, from one second through 720 days; defaults to 365 days.
    /// </summary>
    public TimeSpan Validity { get; set; } = TimeSpan.FromDays(365);
    /// <summary>
    /// The original complete HTTPS push callback URL, at most 512 UTF-8 bytes, or <see langword="null"/> to omit the declaration.
    /// </summary>
    public string? PushGateway { get; set; }
}
