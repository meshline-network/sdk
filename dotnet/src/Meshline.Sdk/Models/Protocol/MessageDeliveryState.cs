namespace Meshline.Models.Protocol;

/// <summary>
/// Describes the relay's delivery progress for an accepted message.
/// </summary>
public enum MessageDeliveryState
{
    /// <summary>
    /// The relay is still attempting delivery.
    /// </summary>
    Delivering,
    /// <summary>
    /// The destination relay accepted the message; this is not a recipient read receipt.
    /// </summary>
    TargetAccepted,
    /// <summary>
    /// The relay reports a definitive delivery failure.
    /// </summary>
    Failed,
}
