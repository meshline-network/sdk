using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Meshline.Backups.BackupData;

namespace Meshline;

sealed partial class MeshlineClient
{
    /// <summary>Exports a password-encrypted backup of local data.</summary>
    /// <param name="destination">The writable stream, left open. Discard partial output if export fails.</param>
    /// <param name="password">A nonempty password, normalized to Unicode NFC and encoded as UTF-8 by the SDK. Spaces and case are preserved.</param>
    /// <param name="options">Optional direct and group history selections; both default to <see cref="BackupSelection.None"/>.</param>
    /// <param name="cancellationToken">Cancels export; client disposal also cancels it.</param>
    /// <returns>Exported record counts.</returns>
    /// <remarks>
    /// <para>Requires initialization. Contacts, group state and held secrets, and channel entries are always included;
    /// history is selected through <paramref name="options"/>. Unprotecting group secrets requires a local device.</para>
    /// <para>May run alongside ordinary client operations without network I/O. Each component has a consistent
    /// snapshot; inconsistent device verification material across snapshots fails export. Retry with a fresh stream.</para>
    /// <para>Invalid history identifiers and an oversized manifest fail before any destination writes.
    /// Cancellation may wait for an active password derivation to finish.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Initialization is missing, or group secrets require a local device that is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">Client disposal has begun.</exception>
    /// <exception cref="ArgumentException">The password is null, empty, or contains invalid Unicode; or a history selection is null or has invalid identifiers.</exception>
    /// <exception cref="OperationCanceledException">The caller or client lifetime cancels export.</exception>
    /// <exception cref="IOException">Stream access fails, stored backup data is invalid, or device verification material becomes inconsistent during export.</exception>
    public async Task<BackupReport> ExportBackupAsync(Stream destination, string password, BackupOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new();
        if (!ValidSelection(options.DirectHistory, false, cancellationToken) || !ValidSelection(options.GroupHistory, true, cancellationToken))
            throw new ArgumentException("Invalid backup history selection.", nameof(options));
        var manifest = new Manifest(Context.ToString(), Options.AccountId, Clock.UtcNow.ToUnixTimeSeconds(),
            new(options.DirectHistory.Mode, options.DirectHistory.Ids.ToArray()), new(options.GroupHistory.Mode, options.GroupHistory.Ids.ToArray()));
        BackupCodec.ValidateRecordSize("manifest", manifest);
        await using var codec = await BackupCodec.CreateAsync(destination, password, cancellationToken).ConfigureAwait(false);
        await codec.WriteAsync("manifest", manifest, cancellationToken).ConfigureAwait(false);
        var report = new BackupReport();
        var deviceStates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var requiredDeviceStates = new HashSet<string>(StringComparer.Ordinal) { Options.AccountId };
        IAsyncEnumerable<JsonElement>[] streams = [
            DeviceManager.ExportBackupAsync(cancellationToken),
            MessageManager.ExportBackupAsync(options.DirectHistory, cancellationToken),
            GroupManager.ExportBackupAsync(options.GroupHistory, cancellationToken),
            ChannelManager.ExportBackupAsync(cancellationToken)];
        foreach (var stream in streams)
            await foreach (var record in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                CollectBackupDeviceDependencies(record, deviceStates, requiredDeviceStates);
                await codec.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                report.Applied++;
            }
        await ValidateBackupDeviceDependenciesAsync(deviceStates, requiredDeviceStates, cancellationToken).ConfigureAwait(false);
        await codec.WriteAsync("end", new End(report.Applied), cancellationToken).ConfigureAwait(false);
        await codec.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

    /// <summary>Authenticates and incrementally merges a backup into this client and its components.</summary>
    /// <param name="source">The complete backup stream positioned at its header; left open.</param>
    /// <param name="password">The backup password; the SDK applies Unicode NFC and UTF-8 without trimming spaces or changing case.</param>
    /// <param name="cancellationToken">Cancels import, retaining committed records. Disposal of the client or the importing component also cancels it.</param>
    /// <returns>Committed merge counts and conflicts for which local values were retained.</returns>
    /// <remarks>
    /// <para>Requires initialization and a manifest account/network matching the database binding.
    /// Each component commits validated records alongside its ordinary writes. Later failures, including final
    /// authentication errors, retain earlier commits. Retry the file from the beginning to merge the remaining data.
    /// Successful return requires validation of the complete file.</para>
    /// <para>Committed changes update local caches and raise existing component events. Message events include
    /// restored history; read status follows the target conversation's unchanged read position. Open new query
    /// readers to observe imported data. Active contacts enter normal background maintenance after commit.</para>
    /// <para>Import can run while the client is running and performs no network I/O itself. Protecting or unprotecting
    /// group secrets requires an independently created local device. Other records do not require one.</para>
    /// <para>The source need not support seeking. Cancellation may wait for an active password derivation to finish.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The client or a required component is not initialized, or group secrets require a local device that is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">The client or a component is being disposed.</exception>
    /// <exception cref="ArgumentException">The password is null, empty, or contains invalid Unicode.</exception>
    /// <exception cref="OperationCanceledException">The caller or an owned lifetime cancels import; earlier commits are retained.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The password is wrong, file authentication fails, or local secret protection fails.</exception>
    /// <exception cref="IOException">Stream access fails, the file is incomplete, or backup data/bindings are invalid.</exception>
    public async Task<BackupReport> ImportBackupAsync(Stream source, string password, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        ArgumentNullException.ThrowIfNull(source);
        await using var codec = await BackupCodec.OpenAsync(source, password, cancellationToken).ConfigureAwait(false);
        var first = ReadRecord(await codec.ReadAsync(cancellationToken).ConfigureAwait(false));
        if (first.Type != "manifest")
            throw new InvalidDataException("The first backup record must be a manifest.");
        var manifest = Read<Manifest>(first.Data);
        await using (var database = new MeshlineDbContext(_databaseOptions))
        {
            var binding = await database.Bindings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
            if (manifest.Network != binding.Context || manifest.Account != binding.AccountId || manifest.CreatedAt < 0)
                throw new InvalidDataException("The backup belongs to another account/network or has an invalid timestamp.");
        }
        var direct = ReadSelection(manifest.DirectHistory, false, cancellationToken);
        var groupHistory = ReadSelection(manifest.GroupHistory, true, cancellationToken);
        var report = new BackupReport();
        long count = 0;
        var record = await codec.ReadAsync(cancellationToken).ConfigureAwait(false);
        var frame = ReadRecord(record);
        report.Add(await DeviceManager.ImportBackupAsync(ReadComponent(0, cancellationToken), cancellationToken).ConfigureAwait(false));
        report.Add(await MessageManager.ImportBackupAsync(ReadComponent(1, cancellationToken), cancellationToken).ConfigureAwait(false));
        report.Add(await GroupManager.ImportBackupAsync(ReadComponent(2, cancellationToken), cancellationToken).ConfigureAwait(false));
        report.Add(await ChannelManager.ImportBackupAsync(ReadComponent(3, cancellationToken), cancellationToken).ConfigureAwait(false));
        if (frame.Type != "end" || Read<End>(frame.Data).Records != count)
            throw new InvalidDataException("Invalid backup end marker, record order or count.");
        await codec.RequireEndAsync(cancellationToken).ConfigureAwait(false);
        return report;

        async IAsyncEnumerable<JsonElement> ReadComponent(int component, [EnumeratorCancellation] CancellationToken token)
        {
            while (Owner(frame.Type) == component)
            {
                if (frame.Type == "message")
                {
                    var value = Read<MessageData>(frame.Data);
                    if (!direct.Includes(value.Sender == Options.AccountId ? value.Recipient : value.Sender))
                        throw new InvalidDataException("Direct history is outside the manifest selection.");
                }
                else if (frame.Type == "group_event")
                {
                    var value = Read<GroupEventRecord>(frame.Data);
                    if (ProtocolModel.FromJson<TypedProtocolModel>(value.PayloadJson) is GroupMessageEnvelope && !groupHistory.Includes(value.GroupId))
                        throw new InvalidDataException("Group history is outside the manifest selection.");
                }
                yield return record;
                count++;
                record = await codec.ReadAsync(token).ConfigureAwait(false);
                frame = ReadRecord(record);
            }
            if (Owner(frame.Type) < component)
                throw new InvalidDataException("Backup components are out of order.");
        }

        static int Owner(string type) => type switch
        {
            "device_state" => 0,
            "contact" or "message" => 1,
            "group" or "group_nickname" or "group_epoch" or "group_event" or "group_secret" => 2,
            "channel" => 3,
            "end" => 4,
            _ => throw new InvalidDataException("Unsupported backup record: " + type)
        };
    }

    static void CollectBackupDeviceDependencies(JsonElement record, Dictionary<string, byte[]> deviceStates, HashSet<string> requiredDeviceStates)
    {
        var frame = ReadRecord(record);
        if (frame.Type == "device_state")
        {
            var state = Protocol<AccountDeviceState>(frame.Data);
            deviceStates.Add(state.Account, DeviceStateDigest(state));
        }
        else if (frame.Type == "contact")
        {
            var contact = Protocol<ContactRecord>(frame.Data);
            if (contact.Status == ContactRelationshipState.Active)
                requiredDeviceStates.Add(contact.Account);
        }
    }

    async Task ValidateBackupDeviceDependenciesAsync(IReadOnlyDictionary<string, byte[]> deviceStates, IReadOnlySet<string> requiredDeviceStates, CancellationToken cancellationToken)
    {
        // Compare against one fresh SELECT snapshot after every component has finished. Only dependencies
        // of the exported records matter: a contact added after its snapshot must not invalidate this file.
        var remaining = requiredDeviceStates.ToHashSet(StringComparer.Ordinal);
        await using (var database = new MeshlineDbContext(_databaseOptions))
        {
            const string changed = "Device verification material changed during backup export. Discard the output and retry export.";
            var accounts = requiredDeviceStates.ToArray();
            await foreach (var state in database.DeviceStates.AsNoTracking().Where(value => accounts.Contains(value.AccountId))
                .AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!deviceStates.TryGetValue(state.AccountId, out var exported)
                    || !exported.AsSpan().SequenceEqual(DeviceStateDigest(ProtocolModel.FromJson<AccountDeviceState>(state.DocumentJson)!)))
                    throw new IOException(changed);
                remaining.Remove(state.AccountId);
            }
            // A cache absent from both snapshots is allowed; a previously exported state disappearing is a change.
            if (remaining.Any(deviceStates.ContainsKey))
                throw new IOException(changed);
        }
    }

    static byte[] DeviceStateDigest(AccountDeviceState state) => SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJson()));
}
