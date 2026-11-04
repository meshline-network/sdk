namespace Meshline.Models.Client;

/// <summary>A detached local snapshot of the latest profile publication and its pending field changes.</summary>
/// <remarks>Editing <see cref="PendingUpdate"/> does not change SDK state; submit it through <see cref="Meshline.Components.ProfileManager.UpdateProfileAsync"/>.</remarks>
public sealed record ProfileUpdateState
{
    /// <summary>The synchronization status of the latest profile update.</summary>
    public required ProfileUpdateStatus Status { get; init; }
    /// <summary>The merged requested field changes, or <see langword="null"/> when synchronized.</summary>
    /// <remarks>Republication without field edits has an empty update with every field unspecified. Cached or supplied publication snapshots do not create field changes.</remarks>
    public ProfileUpdate? PendingUpdate { get; init; }
    /// <summary>The most recent failure, retained during retries and cleared by new intent or confirmation.</summary>
    public ProfileUpdateError? LastError { get; init; }
    /// <summary>The next automatic attempt's scheduled UTC time, or <see langword="null"/> while publishing, synchronized, or paused.</summary>
    /// <remarks>Automatic retry requires a running component.</remarks>
    public DateTimeOffset? NextAttemptAt { get; init; }
}
