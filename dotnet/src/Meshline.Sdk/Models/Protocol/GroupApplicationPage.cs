using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a page of pending group admission applications.
/// </summary>
public sealed record GroupApplicationPage : ProtocolModel
{
    /// <summary>
    /// The admission application entries in this page.
    /// </summary>
    public required ImmutableArray<GroupApplicationEntry> Applications { get; init; }
    /// <summary>
    /// The relay's continuation cursor, or <see langword="null"/> when no next page is indicated.
    /// </summary>
    public string? Next { get; init; }
}
