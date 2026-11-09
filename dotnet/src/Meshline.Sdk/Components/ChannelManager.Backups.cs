using Meshline.Identity;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static Meshline.Backups.BackupData;

namespace Meshline.Components;

sealed partial class ChannelManager
{
    /// <summary>Exports this component's portable logical records from a consistent local snapshot.</summary>
    /// <remarks>Records are plaintext. Use <see cref="MeshlineClient"/> for encrypted backup files. This operation does not access the network.</remarks>
    /// <param name="cancellationToken">Cancels enumeration; component disposal also cancels it.</param>
    /// <returns>Complete JSON records with <c>type</c> and <c>data</c> fields, independent of internal documents and valid after enumeration ends.</returns>
    public async IAsyncEnumerable<JsonElement> ExportBackupAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await using var database = new MeshlineDbContext(databaseOptions);
        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await foreach (var channel in database.Channels.AsNoTracking().OrderBy(value => value.ChannelId).AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return Record("channel", new ChannelData(channel.ChannelId, channel.RelayId, channel.IsFollowed));
    }

    /// <summary>Incrementally merges this component's logical backup records.</summary>
    /// <remarks>
    /// <para>Each completed record is committed. Failure or cancellation retains earlier records;
    /// retrying the stream merges them without duplicates.</para>
    /// <para>Committed business changes raise existing component events after the write lock is released.
    /// Import performs no network I/O and does not replay message processing.</para>
    /// </remarks>
    /// <param name="records">Complete JSON records with <c>type</c> and <c>data</c> fields, in SDK backup order. The caller must ensure they belong to this account and network, and keep their documents alive while records are processed or supply cloned elements.</param>
    /// <param name="cancellationToken">Cancels import; component disposal also cancels it.</param>
    /// <returns>Counts for committed records.</returns>
    public async Task<BackupReport> ImportBackupAsync(IAsyncEnumerable<JsonElement> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var report = new BackupReport();
        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var frame = ValidateRecord(record, "channel");
            ChannelFollowChangedEventArgs? change = null;
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var committed = new BackupReport();
            try
            {
                await using var database = new MeshlineDbContext(databaseOptions);
                await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false);
                await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
                var value = Read<ChannelData>(frame.Data);
                Check(Identifiers.ValidateChannelId(value.ChannelId));
                Check(RelayIdentity.ValidateRelayId(value.RelayId));
                var current = await database.Channels.FindAsync([value.ChannelId], cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    database.Channels.Add(new()
                    {
                        ChannelId = value.ChannelId,
                        RelayId = value.RelayId,
                        IsFollowed = value.IsFollowed
                    });
                    committed.Applied++;
                    change = new(value.ChannelId, value.IsFollowed);
                }
                else if (current.RelayId != value.RelayId)
                    committed.Conflicts++;
                else
                    committed.Unchanged++;
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _stateGate.Release(); }
            report.Add(committed);
            if (change is not null)
                BackgroundFailure.Notify(() => FollowChanged?.Invoke(this, change));
        }
        return report;
    }
}
