using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static Meshline.Backups.BackupData;

namespace Meshline.Components;

sealed partial class DeviceManager
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
        var contactAccounts = database.Contacts.Where(value => value.State == ContactRelationshipState.Active).Select(value => value.AccountId);
        await foreach (var state in database.DeviceStates.AsNoTracking().Where(value => value.AccountId == Options.AccountId || contactAccounts.Contains(value.AccountId)).OrderBy(value => value.AccountId).AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return Record("device_state", Element(state.DocumentJson));
    }

    /// <summary>Incrementally merges this component's logical backup records.</summary>
    /// <remarks>
    /// <para>Each completed record is committed. Failure or cancellation retains earlier records;
    /// retrying the stream merges them without duplicates.</para>
    /// <para>Committed changes update the normal device-state cache; the local device identity is preserved.
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
            var frame = ValidateRecord(record, "device_state");
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var committed = new BackupReport();
            try
            {
                var state = Protocol<AccountDeviceState>(frame.Data);
                await using (var database = new MeshlineDbContext(databaseOptions))
                {
                    await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await using var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false);
                    await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
                    BackupData.Validate(state, Context);
                    // The owner's old device set must never undo new-device registration.
                    if (state.Account == Options.AccountId)
                        committed.Unchanged++;
                    else
                    {
                        var current = await database.DeviceStates.FindAsync([state.Account], cancellationToken).ConfigureAwait(false);
                        var json = state.ToJson();
                        if (current is null)
                        {
                            database.DeviceStates.Add(new()
                            {
                                AccountId = state.Account,
                                Revision = state.Revision,
                                DocumentJson = json
                            });
                            committed.Applied++;
                        }
                        else if (state.Revision > current.Revision)
                        {
                            current.Revision = state.Revision;
                            current.DocumentJson = json;
                            committed.Applied++;
                        }
                        else if (state.Revision == current.Revision && json != current.DocumentJson)
                            committed.Conflicts++;
                        else
                            committed.Unchanged++;
                    }
                    await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                if (committed.Applied > 0)
                    CacheDeviceState(state);
            }
            finally { _stateGate.Release(); }
            report.Add(committed);
        }
        return report;
    }
}
