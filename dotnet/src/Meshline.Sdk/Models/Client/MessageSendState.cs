namespace Meshline.Models.Client;

/// <summary>
/// Describes a direct-message outbox state or a combination of states for filtering.
/// </summary>
[Flags]
public enum MessageSendState
{
    /// <summary>
    /// No send states are selected by the filter.
    /// </summary>
    None = 0,
    /// <summary>
    /// The message is persisted locally and awaits submission.
    /// </summary>
    Queued = 1,
    /// <summary>
    /// The message is being submitted to a relay.
    /// </summary>
    Submitting = 2,
    /// <summary>
    /// Submission was attempted but its acceptance outcome is not yet known.
    /// </summary>
    SubmissionUnknown = 4,
    /// <summary>
    /// The submitting relay accepted the message for delivery.
    /// </summary>
    RelayAccepted = 8,
    /// <summary>
    /// The destination relay accepted the message; this is not a recipient read receipt.
    /// </summary>
    TargetAccepted = 16,
    /// <summary>
    /// The outgoing operation failed definitively.
    /// </summary>
    Failed = 32,
    /// <summary>
    /// The outgoing operation was canceled locally before confirmed relay acceptance.
    /// </summary>
    Canceled = 64,
    /// <summary>
    /// All concrete send states are selected by the filter.
    /// </summary>
    All = Queued | Submitting | SubmissionUnknown | RelayAccepted | TargetAccepted | Failed | Canceled
}
