using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Text;

namespace Meshline.Components;

sealed partial class MessageManager
{
    delegate Task ContactMessageEffect(MeshlineDbContext database, List<Action> notifications, CancellationToken cancellationToken);

    async Task<ContactMessageEffect?> PrepareContactMessageAsync(StoredMessageRecord message, TypedProtocolModel payload, CancellationToken cancellationToken)
    {
        if (message.PayloadType is not ("meshline.contact.consent" or "meshline.contact.grant" or "meshline.account.contacts.sync" or "meshline.device.state.changed")) return null;
        if (message.Sender == Options.AccountId && message.Recipient != Options.AccountId) return null;
        if (payload.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
        var self = message.Sender == message.Recipient;
        if (self != (payload is AccountContactSync)) throw new InvalidDataException("The contact payload is not permitted for this message account binding.");
        if (self)
        {
            var state = await deviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The current account device state is unavailable.");
            if (state.ValidateDeviceAuthorization(message.SenderDeviceId, Context) is { } authorization) throw new InvalidDataException(authorization.Message);
        }
        var effect = payload switch
        {
            ContactConsent => await PrepareConsentAsync(message, payload, cancellationToken).ConfigureAwait(false),
            ContactGrant => await PrepareGrantAsync(message, payload, cancellationToken).ConfigureAwait(false),
            AccountContactSync => await PrepareContactSyncAsync(message, payload, cancellationToken).ConfigureAwait(false),
            DeviceStateChanged => await PrepareDeviceChangeAsync(message, payload, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException("The contact payload type does not match its stored type.")
        };
        if (self || payload is ContactConsent) return effect;
        var own = await deviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The current account device state is unavailable.");
        return async (database, notifications, token) =>
        {
            var grant = await ReadContactAuthorizationAsync(database, message.Sender, token).ConfigureAwait(false);
            ValidateGrant(grant, own, Context);
            await effect(database, notifications, token).ConfigureAwait(false);
        };
    }

    static async Task<ContactGrant> ReadContactAuthorizationAsync(MeshlineDbContext database, string sender, CancellationToken cancellationToken)
    {
        var contact = await database.Contacts.FindAsync([sender], cancellationToken).ConfigureAwait(false);
        if (contact is not { State: ContactRelationshipState.Active, GrantToJson: { } grantJson }) throw new InvalidDataException("No active local contact grant is available for the sender.");
        return ProtocolModel.FromJson<ContactGrant>(grantJson)!;
    }

    Task<ContactMessageEffect> PrepareConsentAsync(StoredMessageRecord message, TypedProtocolModel payload, CancellationToken cancellationToken)
    {
        var consent = (ContactConsent)payload;
        if (consent.DeviceState.Account != message.Sender || consent.Grant.Grantor != message.Sender || consent.Grant.Grantee != message.Recipient)
            throw new InvalidDataException("The contact consent does not match the envelope accounts.");
        if (consent.DeviceState.ValidateDeviceAuthorization(message.SenderDeviceId, Context) is { } violation)
            throw new InvalidDataException(violation.Message);
        var grant = ValidateGrant(consent.Grant, consent.DeviceState, Context);
        if (!grant.Signatures.ContainsKey(message.SenderDeviceId)) throw new InvalidDataException("The sending device did not sign the contact grant.");
        ContactMessageEffect effect = async (database, notifications, token) =>
        {
            var cached = await database.DeviceStates.FindAsync([message.Sender], token).ConfigureAwait(false);
            var stateJson = consent.DeviceState.ToJson();
            if (cached is not null && (cached.Revision > consent.DeviceState.Revision || cached.Revision == consent.DeviceState.Revision && cached.DocumentJson != stateJson))
                throw new InvalidDataException("The contact consent contains an older or conflicting device state.");
            if (cached is null) database.DeviceStates.Add(new() { AccountId = message.Sender, Revision = consent.DeviceState.Revision, DocumentJson = stateJson });
            else { cached.Revision = consent.DeviceState.Revision; cached.DocumentJson = stateJson; }
            var accountId = message.Sender;
            var record = await database.Contacts.FindAsync([accountId], token).ConfigureAwait(false);
            var waiting = await database.ContactRequests.FindAsync([accountId, ContactRequestDirection.Outgoing], token).ConfigureAwait(false);
            var waitingSend = waiting?.MessageId is { } id ? await database.MessageOutbox.FindAsync([id], token).ConfigureAwait(false) : null;
            if (waitingSend?.State == MessageSendState.Canceled) waiting = null;
            if (record is { State: ContactRelationshipState.Active } || waiting is not null)
            {
                record ??= await GetOrCreateContactAsync(database, accountId, token).ConfigureAwait(false);
                record.State = ContactRelationshipState.Active;
                record.GrantFromJson = MergeGrant(record.GrantFromJson, grant, consent.DeviceState).ToJson();
                if (waiting is not null)
                {
                    record.GrantToJson = ProtocolModel.FromJson<ContactConsent>(waiting.ConsentJson)!.Grant.ToJson();
                    if (waitingSend?.AcceptedAt is not null || waiting.SendState == MessageSendState.TargetAccepted) record.ConfirmedGrantToJson = record.GrantToJson;
                }
                record.UpdatedAt = NextContactTime(record.UpdatedAt);
                await ClearRequestsAsync(database, accountId, notifications, token).ConfigureAwait(false);
                await QueueSyncAsync(database, notifications, record, token).ConfigureAwait(false);
                NotifyContact(notifications, record, ContactChangeKind.Relationship | ContactChangeKind.Authorization);
                return;
            }
            var incoming = await database.ContactRequests.FindAsync([accountId, ContactRequestDirection.Incoming], token).ConfigureAwait(false);
            var added = incoming is null;
            if (incoming is null) database.ContactRequests.Add(incoming = new() { AccountId = accountId, Direction = ContactRequestDirection.Incoming, ConsentJson = consent.ToJson() });
            incoming.ConsentJson = consent.ToJson();
            incoming.Note = consent.Note;
            incoming.MessageId = message.MessageId;
            incoming.CreatedAt = message.CreatedAt;
            var info = RequestSnapshot(incoming);
            notifications.Add(() => ContactRequestChanged?.Invoke(this, new(accountId, ContactRequestDirection.Incoming, info, added ? ContactRequestChangeKind.Added : ContactRequestChangeKind.Updated)));
        };
        return Task.FromResult(effect);
    }

    async Task<ContactMessageEffect> PrepareGrantAsync(StoredMessageRecord message, TypedProtocolModel payload, CancellationToken cancellationToken)
    {
        var grant = (ContactGrant)payload;
        if (grant.Grantor != message.Sender || grant.Grantee != message.Recipient) throw new InvalidDataException("The grant does not match the envelope accounts.");
        var state = message.Sender == Options.AccountId ? await deviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            : await deviceManager.GetDeviceStateAsync(grant, cancellationToken).ConfigureAwait(false);
        if (state is null) throw new InvalidOperationException("The contact device state is unavailable.");
        var verified = ValidateGrant(grant, state, Context);
        return async (database, notifications, token) =>
        {
            var outgoing = message.Sender == Options.AccountId;
            var accountId = outgoing ? message.Recipient : message.Sender;
            var record = await database.Contacts.FindAsync([accountId], token).ConfigureAwait(false);
            if (record is not { State: ContactRelationshipState.Active }) return;
            var current = outgoing ? record.GrantToJson : record.GrantFromJson;
            var next = MergeGrant(current, verified, state).ToJson();
            if (next == current) return;
            if (outgoing) record.GrantToJson = next; else record.GrantFromJson = next;
            record.UpdatedAt = NextContactTime(record.UpdatedAt);
            await QueueSyncAsync(database, notifications, record, token).ConfigureAwait(false);
            NotifyContact(notifications, record, ContactChangeKind.Authorization);
        };
    }

    async Task<ContactMessageEffect> PrepareContactSyncAsync(StoredMessageRecord message, TypedProtocolModel payload, CancellationToken cancellationToken)
    {
        var sync = (AccountContactSync)payload;
        var own = await deviceManager.GetDeviceStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The account device state is unavailable.");
        var verified = new List<(ContactRecord Record, AccountDeviceState? Remote, ContactGrant? To, bool AddedSignature)>();
        foreach (var record in sync.Records)
        {
            if (record.Account == Options.AccountId || record.GrantFromContact is { } from && (from.Grantor != record.Account || from.Grantee != Options.AccountId)
                || record.GrantToContact is { } to && (to.Grantor != Options.AccountId || to.Grantee != record.Account))
                throw new InvalidDataException("The synchronized contact belongs to another account scope.");
            AccountDeviceState? remote = null;
            var current = record;
            if (record.GrantFromContact is { } incoming)
            {
                remote = await deviceManager.GetDeviceStateAsync(incoming, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The contact device state is unavailable.");
                current = current with { GrantFromContact = ValidateGrant(incoming, remote, Context) };
            }
            ContactGrant? supplemented = null;
            var addedSignature = false;
            if (record.GrantToContact is { } outgoing)
            {
                var valid = ValidateGrant(outgoing, own, Context);
                supplemented = await AddSignatureAsync(valid, cancellationToken).ConfigureAwait(false);
                addedSignature = supplemented.ToJson() != valid.ToJson();
            }
            verified.Add((current, remote, supplemented, addedSignature));
        }
        return async (database, notifications, token) =>
        {
            foreach (var item in verified)
            {
                var source = item.Record;
                var record = await database.Contacts.FindAsync([source.Account], token).ConfigureAwait(false);
                if (record is not null && record.UpdatedAt.ToUnixTimeSeconds() > source.UpdatedAt) continue;
                record ??= await GetOrCreateContactAsync(database, source.Account, token).ConfigureAwait(false);
                var before = ToProtocolRecord(record).ToJson();
                record.Alias = source.Alias;
                record.State = source.Status;
                record.UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(source.UpdatedAt);
                if (source.Status == ContactRelationshipState.Deleted)
                {
                    record.GrantFromJson = null;
                    record.GrantToJson = null;
                    record.ConfirmedGrantToJson = null;
                    record.RequiredDeviceRevision = null;
                    await ClearRequestsAsync(database, source.Account, notifications, token).ConfigureAwait(false);
                }
                else
                {
                    if (source.GrantFromContact is { } from) record.GrantFromJson = MergeGrant(record.GrantFromJson, from, item.Remote!).ToJson();
                    if (item.To is { } to) record.GrantToJson = MergeGrant(record.GrantToJson, to, own).ToJson();
                    if (item.AddedSignature && record.GrantToJson == item.To!.ToJson())
                    {
                        record.UpdatedAt = NextContactTime(record.UpdatedAt);
                        await QueueSyncAsync(database, notifications, record, token).ConfigureAwait(false);
                        if (record.GrantFromJson is { } incoming)
                            await EnqueueAsync(database, notifications, source.Account, item.To!, token, ProtocolModel.FromJson<ContactGrant>(incoming)).ConfigureAwait(false);
                    }
                }
                if (before != ToProtocolRecord(record).ToJson())
                    NotifyContact(notifications, record, source.Status == ContactRelationshipState.Deleted ? ContactChangeKind.Deleted : ContactChangeKind.Relationship | ContactChangeKind.Alias | ContactChangeKind.Authorization);
            }
            if (sync.RequestSnapshot == true)
            {
                await database.SaveChangesAsync(token).ConfigureAwait(false);
                string after = "";
                while (true)
                {
                    var records = await database.Contacts.AsNoTracking().Where(value => string.Compare(value.AccountId, after) > 0).OrderBy(value => value.AccountId).Take(128).ToListAsync(token).ConfigureAwait(false);
                    if (records.Count == 0) break;
                    await QueueSnapshotAsync(database, notifications, records, message.SenderDeviceId, token).ConfigureAwait(false);
                    await database.SaveChangesAsync(token).ConfigureAwait(false);
                    foreach (var entry in database.ChangeTracker.Entries<MessageOutboxRecord>().Where(value => value.State == EntityState.Unchanged).ToArray()) entry.State = EntityState.Detached;
                    after = records[^1].AccountId;
                }
            }
        };
    }

    Task<ContactMessageEffect> PrepareDeviceChangeAsync(StoredMessageRecord message, TypedProtocolModel payload, CancellationToken cancellationToken)
    {
        var revision = ((DeviceStateChanged)payload).Revision;
        if (revision < 0) throw new InvalidDataException("The device revision must be nonnegative.");
        ContactMessageEffect effect = async (database, _, token) =>
        {
            if (message.Sender == Options.AccountId) return;
            var contact = await database.Contacts.FindAsync([message.Sender], token).ConfigureAwait(false);
            if (contact is not { State: ContactRelationshipState.Active }) return;
            var cached = await database.DeviceStates.FindAsync([message.Sender], token).ConfigureAwait(false);
            if (cached is not null && cached.Revision >= revision) return;
            contact.RequiredDeviceRevision = Math.Max(contact.RequiredDeviceRevision ?? -1, revision);
        };
        return Task.FromResult(effect);
    }

    ContactGrant MergeGrant(string? previousJson, ContactGrant incoming, AccountDeviceState state)
    {
        if (previousJson is null) return incoming;
        var previous = ProtocolModel.FromJson<ContactGrant>(previousJson)!;
        if (!previous.GetSigningInput(Context).AsSpan().SequenceEqual(incoming.GetSigningInput(Context)))
            return (incoming.ExpiresAt ?? long.MaxValue) > (previous.ExpiresAt ?? long.MaxValue) ? incoming : previous;
        var input = previous.GetSigningInput(Context);
        var signatures = incoming.Signatures.ToBuilder();
        foreach (var certificate in CurrentCertificates(state))
        {
            var id = certificate.GetDeviceId(Context);
            if (previous.Signatures.TryGetValue(id, out var signature) && Ed25519.Verify(signature.AsSpan(), certificate.SigningPublicKey.AsSpan(), input)) signatures[id] = signature;
        }
        return incoming with { Signatures = signatures.ToImmutable() };
    }

    async Task QueueSnapshotAsync(MeshlineDbContext database, List<Action> notifications, IReadOnlyList<ContactStateRecord> records, string recipientDevice, CancellationToken cancellationToken)
    {
        var batch = new List<ContactRecord>();
        var bytes = 0;
        foreach (var record in records)
        {
            var item = ToProtocolRecord(record);
            var size = Encoding.UTF8.GetByteCount(item.ToJson());
            if (size > 131072) throw new InvalidOperationException("A contact record exceeds the synchronization message size.");
            if (batch.Count > 0 && bytes + size > 131072)
            {
                await EnqueueAsync(database, notifications, Options.AccountId, new AccountContactSync { Records = [.. batch] }, cancellationToken, recipientDeviceIds: [recipientDevice]).ConfigureAwait(false);
                batch.Clear();
                bytes = 0;
            }
            batch.Add(item);
            bytes += size;
        }
        if (batch.Count > 0) await EnqueueAsync(database, notifications, Options.AccountId, new AccountContactSync { Records = [.. batch] }, cancellationToken, recipientDeviceIds: [recipientDevice]).ConfigureAwait(false);
    }
}
