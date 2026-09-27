namespace Meshline.Components;

/// <summary>
/// Indicates what the component knows about the current account's establishment.
/// </summary>
public enum AccountState
{
    /// <summary>
    /// Account establishment has not been determined from the available route evidence.
    /// </summary>
    Unknown,
    /// <summary>
    /// The account is represented as not yet established.
    /// </summary>
    NotEstablished,
    /// <summary>
    /// An account route is known.
    /// </summary>
    Established
}
