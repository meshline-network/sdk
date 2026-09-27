using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a page of pending member key recovery requests.
/// </summary>
public sealed record GroupRecoveryPage : ProtocolModel
{
    /// <summary>
    /// The member key recovery entries in this page.
    /// </summary>
    public required ImmutableArray<GroupRecoveryEntry> Requests { get; init; }
    /// <summary>
    /// The relay's continuation cursor, or <see langword="null"/> when no next page is indicated.
    /// </summary>
    public string? Next { get; init; }
}
