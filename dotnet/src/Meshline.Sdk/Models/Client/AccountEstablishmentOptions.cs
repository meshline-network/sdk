namespace Meshline.Models.Client;

/// <summary>
/// Configures initial account establishment and the validity of its device certificate and route.
/// </summary>
public sealed class AccountEstablishmentOptions
{
    /// <summary>
    /// The relay's canonical identifier, or <see langword="null"/> to resume the existing establishment target or select an eligible registry relay.
    /// </summary>
    public string? RelayId { get; set; }
    /// <summary>
    /// The requested device certificate lifetime, from one second through 720 days; defaults to 365 days.
    /// </summary>
    public TimeSpan CertificateValidity { get; set; } = TimeSpan.FromDays(365);
    /// <summary>
    /// The requested account route lifetime, from one second through 3650 days; defaults to 365 days.
    /// </summary>
    public TimeSpan RouteValidity { get; set; } = TimeSpan.FromDays(365);
}
