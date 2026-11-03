namespace Meshline.Models.Client;

/// <summary>Diagnostics from the most recent failed attempt of the latest profile update.</summary>
public sealed record ProfileUpdateError
{
    /// <summary>The diagnostic error message.</summary>
    public required string Message { get; init; }
    /// <summary>The relay's symbolic error code, or null for a local or transport failure.</summary>
    public string? Code { get; init; }
    /// <summary>The UTC time at which the attempt failed.</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}
