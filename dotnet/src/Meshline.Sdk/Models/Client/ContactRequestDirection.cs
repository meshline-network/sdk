namespace Meshline.Models.Client;

/// <summary>
/// Selects incoming requests, outgoing requests, or both.
/// </summary>
[Flags]
public enum ContactRequestDirection
{
    /// <summary>
    /// Requests received from other accounts.
    /// </summary>
    Incoming = 1,
    /// <summary>
    /// Requests sent by this account.
    /// </summary>
    Outgoing = 2,
    /// <summary>
    /// Both incoming and outgoing requests.
    /// </summary>
    All = Incoming | Outgoing
}
