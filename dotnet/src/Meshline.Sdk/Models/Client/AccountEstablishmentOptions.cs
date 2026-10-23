namespace Meshline.Models.Client;

/// <summary>
/// Configures idempotent account setup without starting background synchronization.
/// </summary>
/// <remarks>
/// An unfinished setup retains its saved lifetimes and initial profile. Omitted options resume that operation;
/// conflicting explicit relay or profile values are rejected. Valid certificates and routes are retained.
/// </remarks>
public sealed class AccountEstablishmentOptions
{
    /// <summary>
    /// The relay's canonical identifier. An existing route or unfinished operation retains its original relay; a conflicting value is rejected.
    /// </summary>
    public string? RelayId { get; set; }
    /// <summary>
    /// The lifetime for a certificate that needs issuance or renewal, from one second through 720 days; defaults to 365 days.
    /// </summary>
    public TimeSpan CertificateValidity { get; set; } = TimeSpan.FromDays(365);
    /// <summary>
    /// The lifetime for a route that needs publication or renewal, from one second through 3650 days; defaults to 365 days.
    /// </summary>
    public TimeSpan RouteValidity { get; set; } = TimeSpan.FromDays(365);
    /// <summary>
    /// Optional creation-only profile values. Existing profile fields are never changed by account setup.
    /// </summary>
    public AccountInitialProfile? InitialProfile { get; set; }
    /// <summary>
    /// The device explicitly selected for removal while authorizing the local device in one publication.
    /// </summary>
    /// <remarks>
    /// A null value resumes any saved replacement choice; without one, all other devices are preserved.
    /// An unresolved submission or an effective replacement cannot be changed while setup is unfinished.
    /// </remarks>
    public string? ReplaceDeviceId { get; set; }
}
