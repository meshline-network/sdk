namespace Meshline.Models.Client;

/// <summary>
/// Supplies certificate settings for renewing the existing local device without replacing its keys or identity.
/// </summary>
public sealed class DeviceRenewalOptions
{
    /// <summary>
    /// The new certificate lifetime, from one second through 720 days; defaults to 365 days.
    /// </summary>
    public TimeSpan Validity { get; set; } = TimeSpan.FromDays(365);
    /// <summary>
    /// The change to the certificate's push gateway declaration.
    /// </summary>
    /// <remarks>
    /// The default value retains the declaration; assign an original complete HTTPS callback URL
    /// of at most 512 UTF-8 bytes to set it, or use <see cref="FieldUpdate{T}.Delete"/> to remove it. Null and empty assignments are invalid.
    /// </remarks>
    public FieldUpdate<string> PushGateway { get; set; }
}
