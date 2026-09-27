using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Storage;

/// <summary>
/// Creates or upgrades the SDK's SQLite database through EF Core migrations.
/// </summary>
public static class MeshlineDatabase
{
    /// <summary>
    /// Creates or upgrades the SQLite database by applying all pending SDK migrations.
    /// </summary>
    /// <param name="databaseOptions">The SQLite database configuration; create its parent directory and apply migrations before initialization.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// Create the database's parent directory before calling this method. Applications invoke migrations explicitly; component initialization does not perform schema upgrades automatically.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="ArgumentNullException">The database options are null.</exception>
    /// <exception cref="InvalidOperationException">The current EF Core model differs from its migration snapshot or the migration cannot be applied to the current database state.</exception>
    public static async Task MigrateAsync(DatabaseOptions databaseOptions, CancellationToken cancellationToken = default)
    {
        await using var database = new MeshlineDbContext(databaseOptions, SqliteOpenMode.ReadWriteCreate);
        await database.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
