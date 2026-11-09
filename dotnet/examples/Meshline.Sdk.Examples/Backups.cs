using Meshline.Models.Client;

namespace Meshline.Examples;

public static class Backups
{
    #region portable-backup
    public static Task<BackupReport> ExportAsync(
        MeshlineClient client, Stream destination, string password,
        CancellationToken cancellationToken = default)
    {
        // Always exports base data. Both history selections default to None.
        return client.ExportBackupAsync(destination, password, cancellationToken: cancellationToken);
    }

    public static async Task<BackupReport> ImportAsync(
        MeshlineClient client, Stream source, string password,
        CancellationToken cancellationToken = default)
    {
        // The target has its own local device for protecting restored group secrets.
        var report = await client.ImportBackupAsync(source, password, cancellationToken);
        Console.WriteLine($"Import complete: {report.Applied} applied, {report.Unchanged} unchanged, {report.Conflicts} conflicts.");
        return report;
    }
    #endregion

    #region selected-backup-history
    public static Task<BackupReport> ExportSelectedHistoryAsync(
        MeshlineClient client, Stream destination, string password,
        string[] peerAccountIds, string[] groupIds,
        CancellationToken cancellationToken = default) =>
        client.ExportBackupAsync(destination, password, new BackupOptions
        {
            DirectHistory = BackupSelection.Only(peerAccountIds),
            GroupHistory = BackupSelection.Only(groupIds)
        }, cancellationToken);
    #endregion
}
