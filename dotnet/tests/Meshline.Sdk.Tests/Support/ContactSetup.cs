using Meshline.Models.Client;
using Meshline.Models.Protocol;
using System.Collections.Immutable;

namespace Meshline.Tests.Support;

internal static class ContactSetup
{
    internal static async Task AcceptPeerAsync(TestClient fixture, AccountSigner peer, CancellationToken cancellationToken)
    {
        var signer = fixture.Relay.AddPeer(peer);
        var grant = new ContactGrant
        {
            Grantor = peer.AccountId,
            Grantee = fixture.Account.AccountId,
            ExpiresAt = fixture.Relay.Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds(),
            Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
        };
        grant = grant with
        {
            Signatures = grant.Signatures.Add(signer.Certificate.GetDeviceId(TestNetwork.Context), [.. await signer.SignAsync(grant.GetSigningInput(TestNetwork.Context), cancellationToken)])
        };
        var consent = new ContactConsent
        {
            DeviceState = fixture.Relay.Devices[peer.AccountId],
            Grant = grant,
            Note = "hello"
        };
        await using (var db = fixture.Database.Open())
        {
            db.ContactRequests.Add(new()
            {
                AccountId = peer.AccountId,
                Direction = ContactRequestDirection.Incoming,
                ConsentJson = consent.ToJson(),
                CreatedAt = fixture.Relay.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await fixture.Client.MessageManager.AcceptContactRequestAsync(peer.AccountId, cancellationToken);
    }
}
