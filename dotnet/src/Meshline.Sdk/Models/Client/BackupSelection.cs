namespace Meshline.Models.Client;

/// <summary>An immutable selection of all, no, or specified conversations for backup history.</summary>
public sealed class BackupSelection
{
    readonly string[] _ids;
    internal string Mode { get; }
    internal IReadOnlyList<string> Ids { get; }

    BackupSelection(string mode, string[] ids)
    {
        Mode = mode;
        _ids = ids;
        Ids = Array.AsReadOnly(ids);
    }

    /// <summary>Selects all locally stored history of the configured kind.</summary>
    public static BackupSelection All { get; } = new("all", []);

    /// <summary>Excludes history of the configured kind; this is the default for both backup selections.</summary>
    public static BackupSelection None { get; } = new("none", []);

    /// <summary>Selects the specified peers or groups, copying and deduplicating the identifiers.</summary>
    /// <param name="ids">Peer account IDs for direct history, or group IDs for group history. An empty array selects none.</param>
    /// <returns>An immutable selection unaffected by later changes to the input array.</returns>
    /// <remarks>Export validates identifiers for the configured history kind. Valid IDs with no local history contribute no records.</remarks>
    /// <exception cref="ArgumentNullException">The identifier array is null.</exception>
    public static BackupSelection Only(params string[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var copy = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return copy.Length == 0 ? None : new("only", copy);
    }

    internal bool Includes(string id) => Mode == "all" || Mode == "only" && Array.BinarySearch(_ids, id, StringComparer.Ordinal) >= 0;
}
