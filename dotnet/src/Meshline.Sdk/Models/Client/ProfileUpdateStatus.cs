namespace Meshline.Models.Client;

/// <summary>The publication status of the current account's latest local profile intent.</summary>
public enum ProfileUpdateStatus
{
    /// <summary>No local profile publication is pending; this does not imply that a profile exists.</summary>
    Synchronized,
    /// <summary>Saved intent is waiting for an attempt. Start the component to resume background work.</summary>
    Pending,
    /// <summary>The latest intent is being resolved, signed, or published.</summary>
    Publishing,
    /// <summary>An attempt failed and automatic retry is scheduled while the component is running.</summary>
    WaitingToRetry,
    /// <summary>Intent is retained, but automatic retries are paused. Submit an update or republish to retry.</summary>
    Failed
}
