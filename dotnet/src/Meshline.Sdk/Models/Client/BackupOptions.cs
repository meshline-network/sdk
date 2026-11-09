namespace Meshline.Models.Client;

/// <summary>Controls the optional parts of an SDK data backup.</summary>
public sealed class BackupOptions
{
    /// <summary>Selects locally stored direct history by peer account ID; defaults to <see cref="BackupSelection.None"/>.</summary>
    /// <remarks>Includes both incoming and outgoing history for each peer, who need not still be a contact.</remarks>
    public BackupSelection DirectHistory { get; init; } = BackupSelection.None;
    /// <summary>Selects locally stored group chat history by group ID; defaults to <see cref="BackupSelection.None"/>.</summary>
    /// <remarks>All group state, management records and locally held group secrets are exported independently of this selection.</remarks>
    public BackupSelection GroupHistory { get; init; } = BackupSelection.None;
}
