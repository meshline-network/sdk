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
    int _deviceStateNotificationPending;
    int _contactMaintenanceRetryPending;
    readonly HashSet<string> _deviceNotificationRetries = new(StringComparer.Ordinal);

    void OnDeviceChanged(object? sender, DeviceChangedEventArgs args) => _refreshRequests.Writer.TryWrite(true);

    void OnOwnDeviceStateChanged(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref _deviceStateNotificationPending, 1);
        _refreshRequests.Writer.TryWrite(true);
    }

    async Task QueueDeviceStateNotificationsAsync(bool allContacts, CancellationToken cancellationToken)
    {
        if (deviceManager.DeviceState is not { } own) return;
        var payload = new DeviceStateChanged { Revision = own.Revision };
        // The refresh loop owns this set. A device change starts a full pass;
        // ordinary retries visit only failed contacts, avoiding duplicate fanout.
        var retry = allContacts ? null : _deviceNotificationRetries.ToArray();
        _deviceNotificationRetries.Clear();
        string after = "";
        while (true)
        {
            List<ContactStateRecord> contacts;
            await using (var database = new MeshlineDbContext(databaseOptions))
            {
                var query = database.Contacts.AsNoTracking().Where(value => value.State == ContactRelationshipState.Active && string.Compare(value.AccountId, after) > 0);
                if (retry is not null) query = query.Where(value => retry.Contains(value.AccountId));
                contacts = await query
                    .OrderBy(value => value.AccountId).Take(128).ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var contact in contacts)
            {
                try
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
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _deviceNotificationRetries.Add(contact.AccountId);
                    Interlocked.Exchange(ref _contactMaintenanceRetryPending, 1);
                    ReportBackgroundError(BackgroundOperation.Synchronize, contact.AccountId, exception);
                }
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
                    var notifyAll = Interlocked.Exchange(ref _deviceStateNotificationPending, 0) != 0;
                    if (notifyAll || _deviceNotificationRetries.Count != 0)
                    {
                        try { await QueueDeviceStateNotificationsAsync(notifyAll, cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            Interlocked.Exchange(ref _deviceStateNotificationPending, 1);
                            Interlocked.Exchange(ref _contactMaintenanceRetryPending, 1);
                            ReportBackgroundError(BackgroundOperation.Synchronize, null, exception);
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
                            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                            {
                                Interlocked.Exchange(ref _contactMaintenanceRetryPending, 1);
                                ReportBackgroundError(BackgroundOperation.Synchronize, contact.AccountId, exception);
                            }
                        }
                        after = contacts[^1].AccountId;
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _contactMaintenanceRetryPending, 1);
                    ReportBackgroundError(BackgroundOperation.Synchronize, null, exception);
                }
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
        if (change.State != MessageSendState.TargetAccepted) return;
        var contact = await database.Contacts.FindAsync([change.Recipient], cancellationToken).ConfigureAwait(false);
        if (contact is not { State: ContactRelationshipState.Active } || deviceManager.DeviceState is not { } own) return;
        var sent = ProtocolModel.FromJson<MessageSendRequest>(change.RequestJson)!;
        var localId = Certificate.GetDeviceId(Context);
        if (sent.Envelope.FromDeviceId != localId && (sent.SenderBoxes is not { } boxes || !boxes.Any(value => value.DeviceId == localId)))
        {
            // Relay acceptance is known, but a newly authorized device cannot read an
            // earlier device's ciphertext without a matching key box. Keep these facts separate.
            notifications.Add(() => ReportBackgroundError(BackgroundOperation.SendMessage, change.MessageId,
                new InvalidOperationException("The accepted message was encrypted for an earlier device and has no key box for the current device; its contact-grant contents cannot be confirmed locally.")));
            return;
        }
        var payload = await ReadSentPayloadAsync(change, cancellationToken).ConfigureAwait(false);
        var grant = payload switch { ContactConsent consent => consent.Grant, ContactGrant updated => updated, _ => null };
        if (grant is null) return;
        contact.ConfirmedGrantToJson = MergeGrant(contact.ConfirmedGrantToJson, grant, own).ToJson();
    }

    async Task MaintainContactAsync(ContactStateRecord contact, CancellationToken cancellationToken)
    {
        var own = deviceManager.DeviceState;
        var incoming = ReadUnexpiredGrant(contact.GrantFromJson);
        long? refreshedRevision = null;
        if (contact.RequiredDeviceRevision is { } required && incoming is not null)
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            refreshedRevision = await database.DeviceStates.AsNoTracking()
                .Where(state => state.AccountId == contact.AccountId && state.Revision >= required)
                .Select(state => (long?)state.Revision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        ContactGrant? supplemented = null;
        if (own is not null && own.ValidateDeviceAuthorization(Certificate.GetDeviceId(Context), Context) is null && ReadUnexpiredGrant(contact.GrantToJson) is { } outgoing)
            supplemented = await AddSignatureAsync(outgoing, cancellationToken).ConfigureAwait(false);
        var changed = supplemented is not null && supplemented.ToJson() != contact.GrantToJson;
        var needsDelivery = supplemented is not null && !ContainsLocalGrantSignature(ReadUnexpiredGrant(contact.ConfirmedGrantToJson), supplemented);
        if (needsDelivery)
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            needsDelivery = !await HasGrantDeliveryAsync(database, contact.AccountId, supplemented!, cancellationToken).ConfigureAwait(false);
        }
        if (refreshedRevision is null && !changed && !needsDelivery) return;
        if (needsDelivery)
        {
            if (incoming is null) throw new InvalidOperationException("The contact's incoming grant is unavailable or expired; the local grant update remains pending.");
            await RefreshSendStateAsync(contact.AccountId, incoming, cancellationToken).ConfigureAwait(false);
        }
        await TransactAsync(async (database, notifications, token) =>
        {
            var current = await database.Contacts.FindAsync([contact.AccountId], token).ConfigureAwait(false);
            if (current is not { State: ContactRelationshipState.Active }) return;
            if (refreshedRevision is not null && current.RequiredDeviceRevision <= refreshedRevision)
            {
                current.RequiredDeviceRevision = null;
                NotifyContact(notifications, current, ContactChangeKind.Authorization);
            }
            if (current.GrantToJson != contact.GrantToJson || current.GrantFromJson != contact.GrantFromJson)
            {
                Interlocked.Exchange(ref _contactMaintenanceRetryPending, 1);
                return;
            }
            if (changed)
            {
                current.GrantToJson = supplemented!.ToJson();
                current.UpdatedAt = NextContactTime(current.UpdatedAt);
                await QueueSyncAsync(database, notifications, current, token).ConfigureAwait(false);
                NotifyContact(notifications, current, ContactChangeKind.Authorization);
            }
            if (needsDelivery && !ContainsLocalGrantSignature(ReadUnexpiredGrant(current.ConfirmedGrantToJson), supplemented!)
                && !await HasGrantDeliveryAsync(database, current.AccountId, supplemented!, token).ConfigureAwait(false))
                await EnqueueAsync(database, notifications, current.AccountId, supplemented!, token, incoming).ConfigureAwait(false);
        }, cancellationToken, contactAuthorization: true).ConfigureAwait(false);
    }

    bool ContainsLocalGrantSignature(ContactGrant? candidate, ContactGrant issued)
    {
        var deviceId = Certificate.GetDeviceId(Context);
        return candidate is not null && candidate.Signatures.TryGetValue(deviceId, out var signature)
            && issued.Signatures.TryGetValue(deviceId, out var expected) && signature.AsSpan().SequenceEqual(expected.AsSpan())
            && candidate.GetSigningInput(Context).AsSpan().SequenceEqual(issued.GetSigningInput(Context));
    }

    async Task ValidateContactAuthorizationsAsync(MeshlineDbContext database, CancellationToken cancellationToken)
    {
        // Preparation can precede waiting for a device mutation. Validate again
        // inside the coordinated transaction against the newly persisted state.
        var grants = database.ChangeTracker.Entries<ContactStateRecord>()
            .Where(entry => entry.State == EntityState.Added || entry.State == EntityState.Modified && entry.Property(value => value.GrantToJson).IsModified)
            .Where(entry => entry.Entity.State == ContactRelationshipState.Active && entry.Entity.GrantToJson != null)
            .Select(entry => ProtocolModel.FromJson<ContactGrant>(entry.Entity.GrantToJson!)!).ToList();
        grants.AddRange(database.ChangeTracker.Entries<ContactRequestRecord>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.Direction == ContactRequestDirection.Outgoing)
            .Select(entry => ProtocolModel.FromJson<ContactConsent>(entry.Entity.ConsentJson)!.Grant));
        if (grants.Count == 0) return;
        var stored = await database.DeviceStates.FindAsync([Options.AccountId], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The current device state is unavailable for contact authorization.");
        var own = ProtocolModel.FromJson<AccountDeviceState>(stored.DocumentJson)!;
        foreach (var grant in grants)
        {
            if (grant.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()) continue;
            if (FilterGrantSignatures(grant, own, Context).Signatures.IsEmpty)
                throw new InvalidOperationException("Device authorization changed while preparing the contact grant. Prepare it again before retrying.");
        }
    }

    async Task<bool> HasGrantDeliveryAsync(MeshlineDbContext database, string accountId, ContactGrant issued, CancellationToken cancellationToken)
    {
        // Reuse the durable request even when its submission outcome is unknown.
        // A definitive failure remains observable rather than generating a new request on every scan.
        var deliveries = await database.MessageOutbox.AsNoTracking()
            .Where(value => !value.IsDirect && value.Recipient == accountId && value.State != MessageSendState.Canceled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var delivery in deliveries)
        {
            if (ProtocolModel.FromJson<MessageSendRequest>(delivery.RequestJson)!.Envelope.FromDeviceId != Certificate.GetDeviceId(Context)) continue;
            var payload = await ReadSentPayloadAsync(delivery, cancellationToken).ConfigureAwait(false);
            var grant = payload switch { ContactGrant updated => updated, ContactConsent consent => consent.Grant, _ => null };
            if (ContainsLocalGrantSignature(grant, issued)) return true;
        }
        return false;
    }
}
