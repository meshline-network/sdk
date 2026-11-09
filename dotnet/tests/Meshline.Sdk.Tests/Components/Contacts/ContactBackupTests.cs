using Meshline.Backups;
using Meshline.Components;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Tests.Components.Contacts;

public sealed class ContactBackupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Fact]
    public async Task Contact_restore_notifies_classified_committed_changes_without_duplicates_or_conflicts()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(source, peer, Token);
        var records = new List<JsonElement>();
        await foreach (var record in source.Client.MessageManager.ExportBackupAsync(cancellationToken: Token))
            records.Add(record);
        var original = ProtocolModel.FromJson<ContactRecord>(Assert.Single(records).GetProperty("data").GetRawText())! with
        {
            Alias = "original"
        };
        var renamed = original with
        {
            Alias = "renamed",
            UpdatedAt = original.UpdatedAt + 1
        };
        var grantOmitted = renamed with
        {
            GrantFromContact = null,
            UpdatedAt = renamed.UpdatedAt + 1
        };
        var deleted = grantOmitted with
        {
            Status = ContactRelationshipState.Deleted,
            GrantToContact = null,
            UpdatedAt = grantOmitted.UpdatedAt + 1
        };
        var notifications = new List<(object? Sender, ContactChangedEventArgs Change)>();
        var committedStates = new List<ContactRelationshipState>();
        target.Client.MessageManager.ContactChanged += (sender, change) =>
        {
            notifications.Add((sender, change));
            using var db = target.Database.Open();
            committedStates.Add(db.Contacts.Single(row => row.AccountId == peer.AccountId).State);
        };
        var requests = target.Relay.Requests.Count;
        await target.Client.MessageManager.ImportBackupAsync(Replay(new[] { original, renamed, grantOmitted, deleted }
            .Select(value => BackupData.Record("contact", BackupData.Element(value.ToJson())))), Token);
        Assert.Equal(new[] { ContactChangeKind.Relationship | ContactChangeKind.Alias | ContactChangeKind.Authorization,
            ContactChangeKind.Alias, ContactChangeKind.Deleted | ContactChangeKind.Authorization },
            notifications.Select(value => value.Change.ChangeKind));
        Assert.Equal(new[] { ContactRelationshipState.Active, ContactRelationshipState.Active, ContactRelationshipState.Deleted }, committedStates);
        Assert.All(notifications, value => { Assert.Same(target.Client.MessageManager, value.Sender); Assert.Equal(peer.AccountId, value.Change.AccountId); });
        Assert.Equal("renamed", notifications[1].Change.Contact!.Alias);
        Assert.Null(notifications[^1].Change.Contact);
        Assert.Equal(0, (await target.Client.MessageManager.ImportBackupAsync(Replay([BackupData.Record("contact", BackupData.Element(deleted.ToJson()))]), Token)).Applied);
        var conflict = deleted with
        {
            Alias = "conflict"
        };
        Assert.Equal(1, (await target.Client.MessageManager.ImportBackupAsync(Replay([BackupData.Record("contact", BackupData.Element(conflict.ToJson()))]), Token)).Conflicts);
        Assert.Equal(3, notifications.Count);
        Assert.Equal(requests, target.Relay.Requests.Count);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("grant")]
    [InlineData("deleted")]
    public async Task Contact_restore_preserves_local_delivery_evidence_until_the_relationship_is_deleted(string change)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        var remainingId = remaining.Certificate.GetDeviceId(TestNetwork.Context);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId,
            [fixture.Client.Device!, remaining.Certificate], cancellationToken: Token);
        string confirmed;
        await using (var db = fixture.Database.Open())
        {
            var contact = await db.Contacts.SingleAsync(Token);
            var issued = ProtocolModel.FromJson<ContactGrant>(contact.GrantToJson!)!;
            issued = issued with
            {
                ExpiresAt = fixture.Relay.Clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds()
            };
            issued = issued with
            {
                Signatures = issued.Signatures.SetItem(selectedId, [.. await fixture.Client.DeviceManager.SignAsync(issued.GetSigningInput(TestNetwork.Context), Token)]).Add(remainingId,
                    [.. await remaining.SignAsync(issued.GetSigningInput(TestNetwork.Context), Token)])
            };
            confirmed = issued.ToJson();
            contact.GrantToJson = confirmed;
            contact.ConfirmedGrantToJson = confirmed;
            await db.SaveChangesAsync(Token);
        }
        var records = new List<JsonElement>();
        await foreach (var record in fixture.Client.MessageManager.ExportBackupAsync(cancellationToken: Token))
            records.Add(record);
        var original = ProtocolModel.FromJson<ContactRecord>(Assert.Single(records).GetProperty("data").GetRawText())!;
        var restored = original with
        {
            Alias = "restored alias",
            UpdatedAt = original.UpdatedAt + 1
        };
        if (change == "grant")
        {
            var grant = original.GrantToContact! with
            {
                ExpiresAt = fixture.Relay.Clock.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds()
            };
            var input = grant.GetSigningInput(TestNetwork.Context);
            grant = grant with
            {
                Signatures = grant.Signatures
                    .SetItem(selectedId, [.. await fixture.Client.DeviceManager.SignAsync(input, Token)])
                    .SetItem(remainingId, [.. await remaining.SignAsync(input, Token)])
            };
            restored = restored with
            {
                GrantToContact = grant
            };
        }
        else if (change == "deleted")
            restored = restored with
            {
                Status = ContactRelationshipState.Deleted,
                GrantFromContact = null,
                GrantToContact = null
            };

        var requests = fixture.Relay.Requests.Count;
        Assert.Equal(1, (await fixture.Client.MessageManager.ImportBackupAsync(
            Replay([BackupData.Record("contact", BackupData.Element(restored.ToJson()))]), Token)).Applied);
        Assert.Equal(requests, fixture.Relay.Requests.Count);
        await using (var db = fixture.Database.Open())
        {
            var contact = await db.Contacts.SingleAsync(Token);
            Assert.Equal(restored.GrantToContact?.ToJson(), contact.GrantToJson);
            Assert.Equal(change == "deleted" ? null : confirmed, contact.ConfirmedGrantToJson);
        }

        if (change == "grant")
        {
            // Delivery of the earlier grant cannot confirm a new signed authorization.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token));
            Assert.Contains("replacement_grant_not_confirmed", error.Message, StringComparison.Ordinal);
            Assert.Contains(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
        }
        else
        {
            await fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
            Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
            Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == remainingId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contact_restore_preserves_locally_announced_device_revision_without_adding_a_refresh_requirement(bool hasHint)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var records = new List<JsonElement>();
        await foreach (var record in fixture.Client.MessageManager.ExportBackupAsync(cancellationToken: Token))
            records.Add(record);
        var original = ProtocolModel.FromJson<ContactRecord>(Assert.Single(records).GetProperty("data").GetRawText())!;
        var restored = original with
        {
            Alias = "restored alias",
            UpdatedAt = original.UpdatedAt + 1
        };
        var grant = original.GrantFromContact!;
        var previous = fixture.Relay.Devices[peer.AccountId];
        var newer = previous with
        {
            Revision = previous.Revision + 1,
            AccountSignature = []
        };
        newer = newer with
        {
            AccountSignature = [.. peer.Sign(newer.GetSigningInput(TestNetwork.Context))]
        };

        // A later device notification does not advance the contact's update timestamp.
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(10));
        if (hasHint)
        {
            await using var db = fixture.Database.Open();
            (await db.Contacts.SingleAsync(Token)).RequiredDeviceRevision = newer.Revision;
            await db.SaveChangesAsync(Token);
        }
        var requests = fixture.Relay.Requests.Count;
        Assert.Equal(1, (await fixture.Client.MessageManager.ImportBackupAsync(
            Replay([BackupData.Record("contact", BackupData.Element(restored.ToJson()))]), Token)).Applied);
        Assert.Equal(requests, fixture.Relay.Requests.Count);

        // The relay still returns the old version: only the locally received hint makes it incomplete.
        if (hasHint)
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.DeviceManager.RefreshDeviceStateAsync(grant, Token));
        else
            Assert.Equal(previous.Revision, (await fixture.Client.DeviceManager.RefreshDeviceStateAsync(grant, Token))!.Revision);
        await using (var db = fixture.Database.Open())
        {
            var contact = await db.Contacts.SingleAsync(Token);
            Assert.Equal(restored.Alias, contact.Alias);
            Assert.Equal(hasHint ? newer.Revision : (long?)null, contact.RequiredDeviceRevision);
        }

        fixture.Relay.Devices[peer.AccountId] = newer;
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(newer.Revision, (await fixture.Client.DeviceManager.RefreshDeviceStateAsync(grant, Token))!.Revision);
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
            yield return record;
        await Task.CompletedTask;
    }
}
