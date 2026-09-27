namespace Meshline.Storage;

/// <summary>
/// Specifies the SQLite database file used by one network, account, and local device.
/// </summary>
/// <remarks>
/// Use a separate database for each network, account, and device. Assigning the path does not open a database or create its parent directory. Call <see cref="MeshlineDatabase.MigrateAsync"/> before initializing clients or components.
/// </remarks>
public sealed class DatabaseOptions
{
    /// <summary>
    /// The absolute SQLite file path; relative input is resolved against the current working directory when assigned.
    /// </summary>
    public required string Path
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = System.IO.Path.GetFullPath(value);
        }
    }
}
