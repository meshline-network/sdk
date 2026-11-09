namespace Meshline.Models.Client;

/// <summary>Committed or exported record counts for an SDK backup operation.</summary>
/// <remarks>Each logical record contributes to one counter.
/// A report is returned on successful completion; a failed import can still retain earlier committed records.</remarks>
public sealed class BackupReport
{
    /// <summary>Number of logical records exported or added/updated by import.</summary>
    public long Applied { get; internal set; }
    /// <summary>Number of input records that left local data unchanged.</summary>
    public long Unchanged { get; internal set; }
    /// <summary>Number of conflicting records for which local data was preserved.</summary>
    public long Conflicts { get; internal set; }

    internal void Add(BackupReport report)
    {
        Applied += report.Applied;
        Unchanged += report.Unchanged;
        Conflicts += report.Conflicts;
    }
}
