using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Storage;

/// <summary>
/// Reads successive batches from a fixed snapshot of a local SQLite query.
/// </summary>
/// <typeparam name="T">The type of item returned by the reader.</typeparam>
/// <remarks>
/// The snapshot is fixed when the reader is opened, so later database changes do not reorder or extend its batches. An empty batch marks completion. Open a new reader to refresh and dispose this reader promptly to release its transaction.
/// </remarks>
public sealed class QueryReader<T> : IAsyncDisposable
{
    readonly MeshlineDbContext _database;
    readonly SqliteTransaction _transaction;
    readonly IQueryable<T> _query;
    readonly SemaphoreSlim _gate = new(1, 1);
    int _offset;
    bool _completed;
    bool _disposed;

    QueryReader(MeshlineDbContext database, SqliteTransaction transaction, IQueryable<T> query)
    {
        _database = database;
        _transaction = transaction;
        _query = query;
    }

    internal static async Task<QueryReader<T>> OpenAsync(DatabaseOptions options, Func<MeshlineDbContext, IQueryable<T>> query, CancellationToken cancellationToken)
    {
        var database = new MeshlineDbContext(options, SqliteOpenMode.ReadOnly);
        SqliteTransaction? transaction = null;
        try
        {
            await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
            // The first table read fixes the snapshot before the reader is returned.
            _ = await database.Bindings.AnyAsync(cancellationToken).ConfigureAwait(false);
            return new(database, transaction, query(database));
        }
        catch
        {
            try
            {
                if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// Reads the next batch from the reader's fixed SQLite snapshot.
    /// </summary>
    /// <param name="count">The positive maximum number of items to read in this batch.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>Up to <paramref name="count"/> items in query order, or an empty list when the snapshot is exhausted.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive. A stored sequence or Unix timestamp is outside the range supported by the result model.</exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="OverflowException">Advancing the reader offset exceeds the maximum signed 32-bit item count.</exception>
    /// <exception cref="ArgumentException">A stored account, group, channel, or message identifier is invalid when a result is materialized.</exception>
    /// <exception cref="NotSupportedException">A stored account identifier uses an unsupported namespace when the result is materialized.</exception>
    public async Task<IReadOnlyList<T>> ReadNextAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_completed) return [];
            var items = await _query.Skip(_offset).Take(count).ToListAsync(cancellationToken).ConfigureAwait(false);
            _offset = checked(_offset + items.Count);
            _completed = items.Count < count;
            return items.AsReadOnly();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases the snapshot transaction and its database connection.
    /// </summary>
    /// <returns>A value task that completes when owned resources and active work have been released.</returns>
    /// <exception cref="SqliteException">Releasing the SQLite snapshot transaction fails.</exception>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _database.DisposeAsync().ConfigureAwait(false);
                GC.SuppressFinalize(this);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
