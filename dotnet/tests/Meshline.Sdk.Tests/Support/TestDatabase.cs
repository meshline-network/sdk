using Meshline.Storage;
using Microsoft.Data.Sqlite;

namespace Meshline.Tests.Support;

internal sealed class TestDatabase : IAsyncDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "meshline-sdk-tests", Guid.NewGuid().ToString("N"));
    public DatabaseOptions Options { get; }

    public TestDatabase()
    {
        Options = new()
        {
            Path = Path.Combine(directory, "test.db")
        };
    }

    public async Task MigrateAsync()
    {
        Directory.CreateDirectory(directory);
        await MeshlineDatabase.MigrateAsync(Options);
    }

    public MeshlineDbContext Open() => new(Options);
    public ValueTask DisposeAsync()
    {
        // Clear only this fixture's connection pool, preserving parallel tests.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Options.Path,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        SqliteConnection.ClearPool(connection);
        using var creator = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Options.Path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        SqliteConnection.ClearPool(creator);
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Options.Path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        SqliteConnection.ClearPool(reader);
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
        return ValueTask.CompletedTask;
    }
}
