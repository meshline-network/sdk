using Meshline.Backups;
using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Tests.Components.Contacts;

public sealed class ContactBackupGrantTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alias_backup_keeps_the_targets_current_peer_authorization(bool newerAlias)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var source = Create();
        await source.InitializeAsync();
        await using var target = Create();
        await target.InitializeAsync();
        await using var peerFixture = new TestClient();
        await peerFixture.InitializeAsync();
        var peer = peerFixture.Account;
        await ContactSetup.AcceptPeerAsync(source, peer, Token);
        ContactGrant oldGrant;
        await using (var db = source.Database.Open())
            oldGrant = ProtocolModel.FromJson<ContactGrant>((await db.Contacts.SingleAsync(Token)).GrantFromJson!)!;
        clock.Advance(TimeSpan.FromSeconds(1));
        target.Relay.AddPeer(peer);
        var replacement = peerFixture.Client.Device!;
        var replacementId = replacement.GetDeviceId(TestNetwork.Context);
        Assert.DoesNotContain(replacementId, oldGrant.Signatures.Keys);

        // Target has received the replacement device's signature; source still holds the revoked device's signature.
        var currentState = target.Relay.Devices[peer.AccountId] with
        {
            Revision = 1,
            Certificates = [replacement],
            AccountSignature = []
        };
        target.Relay.Devices[peer.AccountId] = currentState with
        {
            AccountSignature = [.. peer.Sign(currentState.GetSigningInput(TestNetwork.Context))]
        };
        var newGrant = oldGrant with
        {
            Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(replacementId,
                [.. await peerFixture.Client.DeviceManager.SignAsync(oldGrant.GetSigningInput(TestNetwork.Context), Token)])
        };
        await using (var db = target.Database.Open())
        {
            db.ContactRequests.Add(new()
            {
                AccountId = peer.AccountId,
                Direction = ContactRequestDirection.Incoming,
                CreatedAt = clock.GetUtcNow(),
                ConsentJson = new ContactConsent { DeviceState = target.Relay.Devices[peer.AccountId], Grant = newGrant }.ToJson()
            });
            await db.SaveChangesAsync(Token);
        }
        await target.Client.MessageManager.AcceptContactRequestAsync(peer.AccountId, Token);
        await target.Client.DeviceManager.RefreshDeviceStateAsync(peer.AccountId, Token);
        Assert.Equal(ContactGrantState.Valid, (await target.Client.MessageManager.GetContactAsync(peer.AccountId, Token))!.GrantFromContact);
        await target.Client.MessageManager.SendMessageAsync(peer.AccountId,
            new()
            {
                Body = new()
                {
                    ContentType = "text/plain",
                    Text = "before restore"
                }
            }, Token);

        clock.Advance(TimeSpan.FromSeconds(10));
        if (newerAlias)
            await source.Client.MessageManager.SetAliasAsync(peer.AccountId, "newer alias", Token);
        using var backup = new MemoryStream();
        await source.Client.ExportBackupAsync(backup, "contact merge", cancellationToken: Token);
        var requests = target.Relay.Requests.Count;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            backup.Position = 0;
            var report = await target.Client.ImportBackupAsync(backup, "contact merge", Token);
            Assert.Equal(0, report.Conflicts);
            if (attempt > 0)
                Assert.Equal(0, report.Applied);
        }
        Assert.Equal(requests, target.Relay.Requests.Count);
        var restored = await target.Client.MessageManager.GetContactAsync(peer.AccountId, Token);
        Assert.Equal(newerAlias ? "newer alias" : null, restored!.Alias);
        Assert.Equal(ContactGrantState.Valid, restored.GrantFromContact);
        Assert.Equal(newGrant.ToJson(), (await ReadGrantAsync(target, outgoing: false)).ToJson());
        await target.Client.MessageManager.SendMessageAsync(peer.AccountId,
            new()
            {
                Body = new()
                {
                    ContentType = "text/plain",
                    Text = "after restore"
                }
            }, Token);
    }

    [Theory]
    [InlineData(false, "same_time")]
    [InlineData(true, "same_time")]
    [InlineData(false, "newer")]
    [InlineData(true, "newer")]
    [InlineData(false, "invalid_duplicate")]
    [InlineData(true, "invalid_duplicate")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "no_state")]
    [InlineData(true, "no_state")]
    public async Task Grants_merge_independently_of_alias_changes_and_retry_without_notifications(bool outgoing, string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var peer = new TestClient();
        await peer.InitializeAsync();
        var (original, extra, signer) = await SeedAsync(fixture, peer, outgoing);
        var held = (outgoing ? original.GrantToContact : original.GrantFromContact)!;
        var localSignature = Assert.Single(held.Signatures);
        var incoming = scenario == "missing" ? null : extra;
        if (scenario is "invalid_duplicate" or "no_state")
            incoming = extra with
            {
                Signatures = extra.Signatures.Add(localSignature.Key, new byte[64].ToImmutableArray())
            };
        if (scenario == "no_state")
        {
            await using var db = fixture.Database.Open();
            await db.DeviceStates.Where(value => value.AccountId == held.Grantor).ExecuteDeleteAsync(Token);
        }
        var record = WithGrant(original, outgoing, incoming) with
        {
            Alias = scenario == "same_time" ? original.Alias : "renamed",
            UpdatedAt = original.UpdatedAt + (scenario == "same_time" ? 0 : 1)
        };
        var notifications = new List<ContactChangeKind>();
        fixture.Client.MessageManager.ContactChanged += (_, args) => notifications.Add(args.ChangeKind);
        var requests = fixture.Relay.Requests.Count;
        var report = await ImportAsync(fixture, record);
        Assert.Equal(1, report.Applied);
        Assert.Equal(0, report.Conflicts);
        var actual = await ReadGrantAsync(fixture, outgoing);
        Assert.Equal(held.GetSigningInput(TestNetwork.Context), actual.GetSigningInput(TestNetwork.Context));
        Assert.Equal(localSignature.Value, actual.Signatures[localSignature.Key]);
        Assert.Equal(scenario == "missing" ? 1 : 2, actual.Signatures.Count);
        if (scenario != "missing")
            Assert.Equal(extra.Signatures[signer.Certificate.GetDeviceId(TestNetwork.Context)], actual.Signatures[signer.Certificate.GetDeviceId(TestNetwork.Context)]);
        var expectedChange = scenario == "same_time" ? ContactChangeKind.Authorization : ContactChangeKind.Alias;
        if (scenario != "missing")
            expectedChange |= ContactChangeKind.Authorization;
        Assert.Equal(expectedChange, Assert.Single(notifications));
        var repeated = await ImportAsync(fixture, record);
        Assert.Equal(1, repeated.Unchanged);
        Assert.Equal(0, repeated.Conflicts);
        Assert.Single(notifications);
        Assert.Equal(requests, fixture.Relay.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_grant_bodies_follow_expiry_rules_only_after_local_signature_verification(bool outgoing)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var peer = new TestClient();
        await peer.InitializeAsync();
        var (original, extra, signer) = await SeedAsync(fixture, peer, outgoing);
        var expected = (outgoing ? original.GrantToContact : original.GrantFromContact)!;
        var end = expected.ExpiresAt!.Value;
        var requests = fixture.Relay.Requests.Count;
        var timestamp = original.UpdatedAt;
        foreach (var scenario in new[] { "shorter", "same_expiry", "invalid_longer", "unverified_longer", "longer", "unlimited", "finite", "same_unlimited" })
        {
            long? expiry = scenario switch
            {
                "shorter" => end - 60,
                "same_expiry" => end,
                "unlimited" or "same_unlimited" => null,
                _ => end + 60
            };
            var candidate = extra with
            {
                ExpiresAt = expiry
            };
            if (scenario is "same_expiry" or "same_unlimited")
                candidate = candidate with
                {
                    AdditionalProperties = candidate.AdditionalProperties.Add("extra", JsonSerializer.SerializeToElement("different body"))
                };
            candidate = candidate with
            {
                Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(signer.Certificate.GetDeviceId(TestNetwork.Context),
                    scenario == "invalid_longer" ? new byte[64].ToImmutableArray() : [.. await signer.SignAsync(candidate.GetSigningInput(TestNetwork.Context), Token)])
            };
            string? savedState = null;
            if (scenario == "unverified_longer")
            {
                await using var db = fixture.Database.Open();
                var state = await db.DeviceStates.SingleAsync(value => value.AccountId == candidate.Grantor, Token);
                savedState = state.DocumentJson;
                db.DeviceStates.Remove(state);
                await db.SaveChangesAsync(Token);
            }
            if (scenario is "longer" or "unlimited")
                expected = candidate;
            var record = WithGrant(original, outgoing, candidate) with
            {
                Alias = scenario,
                UpdatedAt = ++timestamp
            };
            var report = await ImportAsync(fixture, record);
            Assert.Equal(1, report.Applied);
            Assert.Equal(0, report.Conflicts);
            Assert.Equal(expected.ToJson(), (await ReadGrantAsync(fixture, outgoing)).ToJson());
            Assert.Equal(scenario, (await fixture.Client.MessageManager.GetContactAsync(peer.Account.AccountId, Token))!.Alias);
            Assert.Equal(1, (await ImportAsync(fixture, record)).Unchanged);
            if (savedState is not null)
            {
                var state = ProtocolModel.FromJson<AccountDeviceState>(savedState)!;
                await using var db = fixture.Database.Open();
                db.DeviceStates.Add(new()
                {
                    AccountId = state.Account,
                    Revision = state.Revision,
                    DocumentJson = savedState
                });
                await db.SaveChangesAsync(Token);
            }
        }
        Assert.Equal(requests, fixture.Relay.Requests.Count);
    }

    static async Task<(ContactRecord Original, ContactGrant Extra, DeviceSigner Signer)> SeedAsync(TestClient fixture, TestClient peer, bool outgoing)
    {
        var grantor = outgoing ? fixture : peer;
        var signer = new DeviceSigner(grantor.Account, fixture.Relay.Clock);
        var state = grantor.Client.DeviceState! with
        {
            Revision = 1,
            Certificates = [grantor.Client.Device!, signer.Certificate],
            AccountSignature = []
        };
        state = state with
        {
            AccountSignature = [.. grantor.Account.Sign(state.GetSigningInput(TestNetwork.Context))]
        };
        if (outgoing)
            await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, state.Certificates, cancellationToken: Token);
        else
            await fixture.Client.DeviceManager.ImportBackupAsync(Replay([BackupData.Record("device_state", BackupData.Element(state.ToJson()))]), Token);
        var grant = new ContactGrant
        {
            Grantor = grantor.Account.AccountId,
            Grantee = (outgoing ? peer : fixture).Account.AccountId,
            ExpiresAt = Clock.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
        };
        grant = grant with
        {
            Signatures = grant.Signatures.Add(grantor.Client.Device!.GetDeviceId(TestNetwork.Context), [.. await grantor.Client.DeviceManager.SignAsync(grant.GetSigningInput(TestNetwork.Context), Token)])
        };
        var extra = grant with
        {
            Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(signer.Certificate.GetDeviceId(TestNetwork.Context), [.. await signer.SignAsync(grant.GetSigningInput(TestNetwork.Context), Token)])
        };
        var original = WithGrant(new ContactRecord { Account = peer.Account.AccountId, Alias = "original", Status = ContactRelationshipState.Active, UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds() }, outgoing, grant);
        await ImportAsync(fixture, original);
        return (original, extra, signer);
    }

    static ContactRecord WithGrant(ContactRecord record, bool outgoing, ContactGrant? grant) => outgoing
        ? record with
        {
            GrantToContact = grant
        } : record with
        {
            GrantFromContact = grant
        };

    static Task<BackupReport> ImportAsync(TestClient fixture, ContactRecord record) =>
        fixture.Client.MessageManager.ImportBackupAsync(Replay([BackupData.Record("contact", BackupData.Element(record.ToJson()))]), Token);

    static async Task<ContactGrant> ReadGrantAsync(TestClient fixture, bool outgoing)
    {
        await using var db = fixture.Database.Open();
        var record = await db.Contacts.SingleAsync(Token);
        return ProtocolModel.FromJson<ContactGrant>((outgoing ? record.GrantToJson : record.GrantFromJson)!)!;
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
        {
            Token.ThrowIfCancellationRequested();
            yield return record;
        }
        await Task.CompletedTask;
    }
}
