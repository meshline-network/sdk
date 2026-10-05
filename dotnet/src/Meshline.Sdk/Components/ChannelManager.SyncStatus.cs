using Meshline.Identity;
using Meshline.Models.Client;
using Meshline.Models.Protocol;

namespace Meshline.Components;

sealed partial class ChannelManager
{
    readonly ResourceSyncTracker _syncStatus = new();

    /// <summary>
    /// Performs and awaits one incremental synchronization pass for the specified channel's descriptor and forward timeline.
    /// </summary>
    /// <param name="channel">The resource to synchronize.</param>
    /// <param name="cancellationToken">Cancels this call, including queued work; already committed data is retained.</param>
    /// <returns>This pass's completion snapshot, including any processing block such as missing group keys.</returns>
    /// <remarks>
    /// Requires initialization and usable authorization, but not StartAsync. Every call performs a fresh pass,
    /// serialized with background synchronization. Reads forward from local progress until the relay reports no more pages;
    /// this does not freeze a remote sequence at call time or guarantee complete historical data.
    /// Transport and storage failures update synchronization status and propagate to the caller.
    /// </remarks>
    /// <exception cref="ArgumentException">The resource identifier is invalid.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component is not initialized, or authorization is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">The component has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The call, component lifetime, or relay request was canceled.</exception>
    /// <exception cref="HttpRequestException">Relay discovery or a network request fails.</exception>
    /// <exception cref="Meshline.Transport.RelayException">The relay rejects synchronization.</exception>
    /// <exception cref="InvalidDataException">Remote or stored data fails validation.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Local storage cannot be accessed.</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Persisting synchronized data fails.</exception>
    public async Task<ResourceSyncStatus> SynchronizeAsync(ChannelRef channel, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        ArgumentNullException.ThrowIfNull(channel);
        return await SynchronizeChannelAsync(channel, cancellationToken, manual: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Occurs after the local synchronization snapshot changes. Handlers must not synchronously wait for component operations.
    /// </summary>
    public event EventHandler<ResourceSyncStatus>? SyncStatusChanged;

    /// <summary>
    /// Returns the current local synchronization snapshot for the channel, without network access.
    /// </summary>
    /// <param name="channelId">The canonical resource identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>An immutable runtime snapshot, initially Idle with no successful completion time.</returns>
    /// <exception cref="ArgumentException">The resource identifier is invalid.</exception>
    /// <exception cref="InvalidOperationException">This component has not been initialized.</exception>
    /// <exception cref="ObjectDisposedException">This component has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public Task<ResourceSyncStatus> GetSyncStatusAsync(string channelId, CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(ref cancellationToken);
        EnsureInitialized();
        if (Identifiers.ValidateChannelId(channelId) is { } violation) throw new ArgumentException(violation.Message, nameof(channelId));
        return Task.FromResult(_syncStatus.Get(channelId));
    }

    /// <inheritdoc/>
    protected override ValueTask DisposeAsyncCore()
    {
        _syncStatus.Stop(OnSyncStatusChanged, resetAll: true);
        return base.DisposeAsyncCore();
    }

    void OnSyncStatusChanged(ResourceSyncStatus status)
    {
        if (SyncStatusChanged is not { } handlers) return;
        foreach (EventHandler<ResourceSyncStatus> handler in handlers.GetInvocationList())
        {
            try { handler(this, status); }
            catch (Exception error) { ReportBackgroundError(BackgroundOperation.Synchronize, status.Resource, error); }
        }
    }
}
