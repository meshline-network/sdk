using Meshline.Models.Client;
using Meshline.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Components;

sealed partial class MessageManager
{
    event Action<MessageSendStatus>? SendStatusCommitted;

    /// <summary>
    /// Waits for a locally tracked send to reach an acceptance milestone or finish unsuccessfully.
    /// </summary>
    /// <param name="messageId">The message identifier returned by <see cref="SendMessageAsync"/>.</param>
    /// <param name="targetState">The milestone to await: queued, relay accepted, or target accepted. Defaults to target accepted.</param>
    /// <param name="cancellationToken">A token that cancels only this wait, without canceling the outgoing message.</param>
    /// <returns>The actual status at or beyond the requested milestone, a failed or canceled status, or <see langword="null"/> if no local record or concurrent completion is available.</returns>
    /// <remarks>
    /// Checks retained local status and observes subsequent commits by this manager without polling or issuing network requests.
    /// Later acceptance satisfies an earlier milestone; failed and canceled always end the wait. Inspect the returned state.
    /// Unknown or evicted records return null immediately. Target acceptance is not a recipient read receipt.
    /// Initialize the component before waiting. Waiting does not start the sender; it survives stop/start, but component disposal cancels it.
    /// There is no built-in timeout; use a cancellation token to bound the wait. Changes made by another manager or process are not observed.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The target is not queued, relay accepted, or target accepted, including combined filter flags.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the wait or the component is disposed while waiting.</exception>
    /// <exception cref="InvalidOperationException">The component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">The component has been disposed.</exception>
    /// <exception cref="SqliteException">Reading the local send status fails.</exception>
    public async Task<MessageSendStatus?> WaitForSendStatusAsync(string messageId, MessageSendState targetState = MessageSendState.TargetAccepted, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (targetState is not (MessageSendState.Queued or MessageSendState.RelayAccepted or MessageSendState.TargetAccepted))
            throw new ArgumentOutOfRangeException(nameof(targetState), targetState, "Expected queued, relay accepted, or target accepted.");
        using var operation = BeginOperation(ref cancellationToken);
        var completion = new TaskCompletionSource<MessageSendStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(MessageSendStatus status)
        {
            if (status.MessageId == messageId && (status.State is MessageSendState.Failed or MessageSendState.Canceled or MessageSendState.TargetAccepted
                || targetState == MessageSendState.Queued || targetState == MessageSendState.RelayAccepted && status.State == MessageSendState.RelayAccepted))
                completion.TrySetResult(status);
        }

        // Register before reading so a commit during the initial lookup cannot be missed.
        SendStatusCommitted += Observe;
        try
        {
            MessageSendStatus? current;
            await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var database = new MeshlineDbContext(databaseOptions);
                var record = await database.MessageOutbox.AsNoTracking().SingleOrDefaultAsync(value => value.MessageId == messageId, cancellationToken).ConfigureAwait(false);
                current = record is null ? null : ToStatus(record);
            }
            finally { _databaseGate.Release(); }
            if (current is not null) Observe(current);
            cancellationToken.ThrowIfCancellationRequested();
            if (current is null && !completion.Task.IsCompleted) return null;
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { SendStatusCommitted -= Observe; }
    }
}
