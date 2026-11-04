using Meshline.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

    public async Task<IAsyncDisposable> CreateTriggerAsync(string name, string definition)
    {
        var identifier = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var sql = $"CREATE TRIGGER {identifier} {definition}";
        await using var database = Open();
        await database.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
        return new Trigger(this, identifier);
    }

    sealed class Trigger(TestDatabase owner, string identifier) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var database = owner.Open();
            var sql = $"DROP TRIGGER IF EXISTS {identifier};";
            await database.Database.ExecuteSqlRawAsync(sql, CancellationToken.None);
        }
    }

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
