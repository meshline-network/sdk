using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Meshline.Components;

sealed partial class DeviceManager
{
    // Removal and replacement share one device-owned check of retained evidence
    // against the exact proposed list. No message manager or delivery is involved.
    async Task<IReadOnlyList<ContactAuthorizationIssue>> CheckRemovalContinuityAsync(MeshlineDbContext database, AccountDeviceState state,
        string removedDeviceId, IReadOnlyList<DeviceCertificate> remaining, CancellationToken cancellationToken)
    {
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        var removed = state.Certificates.FirstOrDefault(value => value.GetDeviceId(Context) == removedDeviceId);
        if (removed is null || removed.ExpiresAt <= now) return [];

        var issuedLocally = await database.Bindings.Select(binding => binding.DeviceId == removedDeviceId).SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!issuedLocally) return [new() { Code = "contact_history_unavailable" }];

        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contact in await ReadContactsAsync(database, cancellationToken).ConfigureAwait(false))
        {
            // A newer local grant does not erase an earlier delivered authorization.
            foreach (var json in contact.GrantToJson == contact.ConfirmedGrantToJson
                ? new[] { contact.GrantToJson } : new[] { contact.GrantToJson, contact.ConfirmedGrantToJson })
            {
                var issued = ReadUnexpiredGrant(json, now);
                if (issued is null || !issued.Signatures.ContainsKey(removedDeviceId)) continue;
                if (!HasConfirmedSignature(contact.ConfirmedGrantToJson, contact.AccountId, issued, remaining, now))
                    affected.Add(contact.AccountId);
            }
        }
        var requests = await database.ContactRequests.AsNoTracking().Where(value => value.Direction == ContactRequestDirection.Outgoing
            && value.SendState != MessageSendState.Canceled && value.SendState != MessageSendState.Failed).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var request in requests)
        {
            var consent = ProtocolModel.FromJson<ContactConsent>(request.ConsentJson)
                ?? throw new InvalidDataException("The stored contact consent is null.");
            var grant = consent.Grant;
            if (grant.ExpiresAt <= now || !grant.Signatures.ContainsKey(removedDeviceId)) continue;
            // Queued and unknown submissions must keep their exact certificate,
            // message ID and ciphertext until delivery is resolved or canceled.
            if (request.SendState != MessageSendState.TargetAccepted
                || !HasConfirmedSignature(grant.ToJson(), request.AccountId, grant, remaining, now))
                affected.Add(request.AccountId);
        }
        return affected.Order(StringComparer.Ordinal).Select(accountId => new ContactAuthorizationIssue
        {
            AccountId = accountId,
            Code = "replacement_grant_not_confirmed"
        }).ToArray();
    }

    bool HasConfirmedSignature(string? confirmedJson, string accountId, ContactGrant issued, IReadOnlyList<DeviceCertificate> certificates, long now)
    {
        var confirmed = ReadUnexpiredGrant(confirmedJson, now);
        if (confirmed is null || confirmed.Validate(Context) is not null || confirmed.Grantee != accountId || confirmed.Grantor != Options.AccountId) return false;
        var input = confirmed.GetSigningInput(Context);
        // An older accepted grant is not confirmation of the currently issued grant.
        if (!input.AsSpan().SequenceEqual(issued.GetSigningInput(Context))) return false;
        return certificates.Any(certificate => certificate.Account == confirmed.Grantor && certificate.NotBefore <= now && certificate.ExpiresAt > now
            && confirmed.Signatures.TryGetValue(certificate.GetDeviceId(Context), out var signature)
            && Ed25519.Verify(signature.AsSpan(), certificate.SigningPublicKey.AsSpan(), input));
    }

    static Task<List<ContactStateRecord>> ReadContactsAsync(MeshlineDbContext database, CancellationToken cancellationToken) =>
        database.Contacts.AsNoTracking().Where(value => value.State == ContactRelationshipState.Active && (value.GrantToJson != null || value.ConfirmedGrantToJson != null))
            .ToListAsync(cancellationToken);

    static ContactGrant? ReadUnexpiredGrant(string? json, long now)
    {
        if (json is null) return null;
        var grant = ProtocolModel.FromJson<ContactGrant>(json) ?? throw new InvalidDataException("The stored contact grant is JSON null.");
        return grant.ExpiresAt <= now ? null : grant;
    }
}
