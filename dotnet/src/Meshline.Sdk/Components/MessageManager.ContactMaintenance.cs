using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;

namespace Meshline.Components;

sealed partial class MessageManager
{
    readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    Task _refresh = Task.CompletedTask;
    Task _poll = Task.CompletedTask;
    int _deviceStateNotificationPending;

    async Task PollContactsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Clock.Provider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) _refreshRequests.Writer.TryWrite(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    void OnDeviceChanged(object? sender, DeviceChangedEventArgs args) => _refreshRequests.Writer.TryWrite(true);

    void OnOwnDeviceStateChanged(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref _deviceStateNotificationPending, 1);
        _refreshRequests.Writer.TryWrite(true);
    }

    async Task QueueDeviceStateNotificationsAsync(CancellationToken cancellationToken)
    {
        if (deviceManager.DeviceState is not { } own) return;
        var payload = new DeviceStateChanged { Revision = own.Revision };
        string after = "";
        while (true)
        {
            List<ContactStateRecord> contacts;
            await using (var database = new MeshlineDbContext(databaseOptions))
                contacts = await database.Contacts.AsNoTracking().Where(value => value.State == ContactRelationshipState.Active && string.Compare(value.AccountId, after) > 0)
                    .OrderBy(value => value.AccountId).Take(128).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var contact in contacts)
            {
                if (ReadUnexpiredGrant(contact.GrantFromJson) is not { } authorization) continue;
                await RefreshSendStateAsync(contact.AccountId, authorization, cancellationToken).ConfigureAwait(false);
                await TransactAsync(async (database, notifications, token) =>
                {
                    var current = await database.Contacts.FindAsync([contact.AccountId], token).ConfigureAwait(false);
                    if (current is { State: ContactRelationshipState.Active } && current.GrantFromJson == contact.GrantFromJson)
                        await EnqueueAsync(database, notifications, contact.AccountId, payload, token, authorization).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            if (contacts.Count < 128) return;
            after = contacts[^1].AccountId;
        }
    }

    async Task RefreshContactsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _refreshRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_refreshRequests.Reader.TryRead(out _)) { }
                try
                {
                    if (Interlocked.Exchange(ref _deviceStateNotificationPending, 0) != 0)
                    {
                        try { await QueueDeviceStateNotificationsAsync(cancellationToken).ConfigureAwait(false); }
                        catch
                        {
                            Interlocked.Exchange(ref _deviceStateNotificationPending, 1);
                            throw;
                        }
                    }
                    string after = "";
                    while (true)
                    {
                        List<ContactStateRecord> contacts;
                        await using (var database = new MeshlineDbContext(databaseOptions))
                            contacts = await database.Contacts.AsNoTracking().Where(value => value.State == ContactRelationshipState.Active && string.Compare(value.AccountId, after) > 0)
                                .OrderBy(value => value.AccountId).Take(128).ToListAsync(cancellationToken).ConfigureAwait(false);
                        if (contacts.Count == 0) break;
                        foreach (var contact in contacts)
                        {
                            try { await MaintainContactAsync(contact, cancellationToken).ConfigureAwait(false); }
                            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, contact.AccountId, exception); }
                        }
                        after = contacts[^1].AccountId;
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportBackgroundError(BackgroundOperation.Synchronize, null, exception); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    async Task ProcessContactSendChangeAsync(MeshlineDbContext database, MessageOutboxRecord change, List<Action> notifications, CancellationToken cancellationToken)
    {
        if (change.IsDirect || change.Recipient == Options.AccountId) return;
        var request = await database.ContactRequests.FindAsync([change.Recipient, ContactRequestDirection.Outgoing], cancellationToken).ConfigureAwait(false);
        if (request?.MessageId == change.MessageId)
        {
            request.SendState = change.State;
            if (change.State == MessageSendState.Canceled)
            {
                database.ContactRequests.Remove(request);
                notifications.Add(() => ContactRequestChanged?.Invoke(this, new(change.Recipient, ContactRequestDirection.Outgoing, null, ContactRequestChangeKind.Removed)));
            }
            else if (change.State != MessageSendState.Queued)
            {
                var snapshot = RequestSnapshot(request);
                notifications.Add(() => ContactRequestChanged?.Invoke(this, new(change.Recipient, ContactRequestDirection.Outgoing, snapshot, ContactRequestChangeKind.Updated)));
            }
        }
        if (change.AcceptedAt is null) return;
        var contact = await database.Contacts.FindAsync([change.Recipient], cancellationToken).ConfigureAwait(false);
        if (contact is not { State: ContactRelationshipState.Active } || deviceManager.DeviceState is not { } own) return;
        var payload = await ReadSentPayloadAsync(change, cancellationToken).ConfigureAwait(false);
        var grant = payload switch { ContactConsent consent => consent.Grant, ContactGrant updated => updated, _ => null };
        if (grant is null) return;
        contact.ConfirmedGrantToJson = MergeGrant(contact.ConfirmedGrantToJson, grant, own).ToJson();
    }

    async Task MaintainContactAsync(ContactStateRecord contact, CancellationToken cancellationToken)
    {
        var own = deviceManager.DeviceState;
        var incoming = ReadUnexpiredGrant(contact.GrantFromJson);
        AccountDeviceState? refreshed = null;
        if (contact.RequiredDeviceRevision is { } required && incoming is not null)
        {
            refreshed = await deviceManager.GetDeviceStateAsync(incoming, cancellationToken).ConfigureAwait(false);
            if (refreshed is null || refreshed.Revision < required) throw new InvalidDataException("The contact device state has not reached its announced revision.");
        }
        ContactGrant? supplemented = null;
        if (own is not null && own.ValidateDeviceAuthorization(Certificate.GetDeviceId(Context), Context) is null && ReadUnexpiredGrant(contact.GrantToJson) is { } outgoing)
            supplemented = await AddSignatureAsync(outgoing, cancellationToken).ConfigureAwait(false);
        if (refreshed is null && (supplemented is null || supplemented.ToJson() == contact.GrantToJson)) return;
        if (supplemented is not null && supplemented.ToJson() != contact.GrantToJson && incoming is not null)
            await RefreshSendStateAsync(contact.AccountId, incoming, cancellationToken).ConfigureAwait(false);
        await TransactAsync(async (database, notifications, token) =>
        {
            var current = await database.Contacts.FindAsync([contact.AccountId], token).ConfigureAwait(false);
            if (current is not { State: ContactRelationshipState.Active }) return;
            if (refreshed is not null && current.RequiredDeviceRevision <= refreshed.Revision)
            {
                current.RequiredDeviceRevision = null;
                NotifyContact(notifications, current, ContactChangeKind.Authorization);
            }
            if (supplemented is not null && current.GrantToJson == contact.GrantToJson && supplemented.ToJson() != current.GrantToJson)
            {
                current.GrantToJson = supplemented.ToJson();
                current.UpdatedAt = NextContactTime(current.UpdatedAt);
                await QueueSyncAsync(database, notifications, current, token).ConfigureAwait(false);
                if (incoming is not null) await EnqueueAsync(database, notifications, current.AccountId, supplemented, token, incoming).ConfigureAwait(false);
                NotifyContact(notifications, current, ContactChangeKind.Authorization);
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
