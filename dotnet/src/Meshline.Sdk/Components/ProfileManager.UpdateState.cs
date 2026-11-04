using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Components;

sealed partial class ProfileManager
{
    /// <summary>Occurs when the pending profile update or its publication state changes.</summary>
    /// <remarks>Read <see cref="GetUpdateStateAsync"/> for the latest snapshot. Handlers run synchronously on the thread performing the change; marshal UI work to the application's dispatcher. Subscriber exceptions are reported through <see cref="ClientComponent.BackgroundError"/> and do not change publication results.</remarks>
    public event EventHandler? UpdateStateChanged;

    /// <summary>Reads the current account's pending profile changes and publication status without network access.</summary>
    /// <param name="cancellationToken">A token that can cancel the local read.</param>
    /// <returns>A detached snapshot of pending field changes, the latest error, and the retry schedule.</returns>
    /// <remarks>Available after initialization, including before <see cref="ClientComponent.StartAsync"/>. Subscribe to <see cref="UpdateStateChanged"/> before the initial read, and serialize subsequent reads so an older result cannot replace a newer view. Pending fields distinguish omission, assignment and deletion.</remarks>
    /// <exception cref="InvalidOperationException">The component has not completed initialization.</exception>
    /// <exception cref="ObjectDisposedException">The component has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    /// <exception cref="SqliteException">The local database cannot be read.</exception>
    /// <exception cref="JsonException">Stored publication data is invalid.</exception>
    public async Task<ProfileUpdateState> GetUpdateStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        return await Updates.GetStateAsync(cancellationToken).ConfigureAwait(false);
    }

    void NotifyUpdateStateChanged()
    {
        if (UpdateStateChanged is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception error)
            {
                try { ReportBackgroundError(BackgroundOperation.Synchronize, AccountId, error); }
                catch { /* Observer failures cannot turn a committed publication into a failed attempt. */ }
            }
        }
    }

    static ProfileUpdateState ReadUpdateState(SignedRequestRecord? row, bool publishing, DateTimeOffset? next)
    {
        if (row is not { Pending: true })
            return new() { Status = ProfileUpdateStatus.Synchronized };
        var error = row.LastErrorJson is null ? null : JsonSerializer.Deserialize<ProfileUpdateError>(row.LastErrorJson);
        var intent = row.IntentJson is null ? new JsonObject() : JsonNode.Parse(row.IntentJson)!.AsObject();
        // Publication snapshots are candidate bases, not requested field changes.
        return new()
        {
            Status = publishing ? ProfileUpdateStatus.Publishing : next is null ? ProfileUpdateStatus.Failed
                : error is null ? ProfileUpdateStatus.Pending : ProfileUpdateStatus.WaitingToRetry,
            PendingUpdate = new()
            {
                Nickname = ReadField<string>(intent, "nickname"),
                Bio = ReadField<string>(intent, "bio"),
                Avatar = ReadField(intent, "avatar", value => ProtocolModel.FromJson<ContentReference>(value.ToJsonString())!),
                PublicDiscovery = ReadField<bool>(intent, "public_discovery")
            },
            LastError = error,
            NextAttemptAt = publishing ? null : next
        };
    }

    static FieldUpdate<T> ReadField<T>(JsonObject fields, string name, Func<JsonNode, T>? read = null) where T : notnull =>
        !fields.TryGetPropertyValue(name, out var value) ? default : value is null ? FieldUpdate<T>.Delete
            : new(read is null ? value.Deserialize<T>()! : read(value));
}
