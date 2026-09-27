namespace Meshline.Models.Protocol;

/// <summary>
/// Describes how an account route compares with another route for the same account.
/// </summary>
public enum RouteComparison
{
    /// <summary>
    /// This route has a lower revision than the other route.
    /// </summary>
    Older,
    /// <summary>
    /// Both routes have the same revision and equal content after excluding account and relay signatures.
    /// </summary>
    Equivalent,
    /// <summary>
    /// This route has a higher revision than the other route.
    /// </summary>
    Newer,
    /// <summary>
    /// Both routes have the same revision but different content after excluding signatures.
    /// </summary>
    Conflict
}
