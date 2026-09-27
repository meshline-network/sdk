using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;

namespace Meshline;

sealed partial class MeshlineClient
{
    readonly Lock _conversationGate = new();
    readonly HashSet<string> _pendingConversations = new(StringComparer.Ordinal);
    readonly Channel<bool> _conversationRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly CancellationTokenSource _conversationLifetime = new();
    HashSet<string> _conversationIds = new(StringComparer.Ordinal);
    Task _conversationObserver = Task.CompletedTask;

    async Task InitializeConversationsAsync(CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(_databaseOptions);
        _conversationIds = new(await QueryConversationIds(database, Options.AccountId).ToListAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
        _conversationObserver = ObserveConversationsAsync(_conversationLifetime.Token);
    }

    void OnMessageReceived(object? sender, MessageReceivedEventArgs args) =>
        QueueConversationChanges(args.Messages.Select(value => value.Key.Sender == Options.AccountId ? value.Recipient : value.Key.Sender));

    void OnMessageSendStatusChanged(object? sender, MessageSendStatusChangedEventArgs args)
    {
        if (args.Status.State == MessageSendState.Queued) QueueConversationChanges([args.Status.Recipient]);
    }

    void OnChannelTimelineChanged(object? sender, ChannelTimelineChangedEventArgs args) => QueueConversationChanges([args.Ref.ChannelId]);

    void OnChannelFollowChanged(object? sender, ChannelFollowChangedEventArgs args) => QueueConversationChanges([args.ChannelId]);

    void OnGroupTimelineChanged(object? sender, GroupTimelineChangedEventArgs args) => QueueConversationChanges(args.Messages.Select(value => value.Group.GroupId));

    void OnGroupChanged(object? sender, GroupChangedEventArgs args) => QueueConversationChanges([args.Group.Ref.GroupId]);

    void QueueConversationChanges(IEnumerable<string> conversationIds)
    {
        lock (_conversationGate) _pendingConversations.UnionWith(conversationIds);
        _conversationRequests.Writer.TryWrite(true);
    }

    async Task ObserveConversationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _conversationRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_conversationRequests.Reader.TryRead(out _)) { }
                string[] ids;
                lock (_conversationGate)
                {
                    ids = _pendingConversations.ToArray();
                    _pendingConversations.Clear();
                }
                if (ids.Length == 0) continue;
                HashSet<string> present;
                try
                {
                    await using var database = new MeshlineDbContext(_databaseOptions);
                    present = new(await QueryConversationIds(database, Options.AccountId).Where(value => ids.Contains(value)).ToListAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    ReportBackgroundError(BackgroundOperation.Synchronize, null, exception);
                    QueueConversationChanges(ids);
                    await Task.Delay(TimeSpan.FromSeconds(1), Clock.Provider, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var changes = new List<ConversationChangedEventArgs>();
                foreach (var id in ids)
                {
                    if (present.Contains(id))
                        changes.Add(new(id, _conversationIds.Add(id) ? ConversationChangeKind.Created : ConversationChangeKind.Updated));
                    else if (_conversationIds.Remove(id))
                        changes.Add(new(id, ConversationChangeKind.Removed));
                }
                foreach (var change in changes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ConversationChanged?.Invoke(this, change);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
