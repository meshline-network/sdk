using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Tests.Components.Contacts;

public sealed class ContactBackupMaintenanceTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Imported_contacts_receive_device_notifications_and_grant_delivery_through_normal_maintenance(bool importWhileRunning)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var first = new AccountSigner();
        using var second = new AccountSigner();
        var restoredPeer = string.CompareOrdinal(first.AccountId, second.AccountId) < 0 ? first : second;
        var sentinel = ReferenceEquals(restoredPeer, first) ? second : first;
        await ContactSetup.AcceptPeerAsync(fixture, restoredPeer, Token);
        await ContactSetup.AcceptPeerAsync(fixture, sentinel, Token);
        var records = new List<JsonElement>();
        await foreach (var record in fixture.Client.MessageManager.ExportBackupAsync(cancellationToken: Token))
            if (record.GetProperty("data").GetProperty("account").GetString() == restoredPeer.AccountId)
                records.Add(record);
        var revision = fixture.Client.DeviceState!.Revision;
        await using (var db = fixture.Database.Open())
        {
            await db.Contacts.Where(value => value.AccountId == restoredPeer.AccountId).ExecuteDeleteAsync(Token);
            await db.MessageOutbox.ExecuteDeleteAsync(Token);
            var retained = await db.Contacts.SingleAsync(Token);
            retained.ConfirmedGrantToJson = null;
            retained.DeviceNotificationRevision = revision;
            await db.SaveChangesAsync(Token);
        }

        var manager = fixture.Client.MessageManager;
        if (importWhileRunning)
        {
            await manager.StartAsync(Token);
            // This is the last account in the startup scan. Its grant delivery proves
            // that maintenance has examined its snapshot. The imported account sorts
            // before it, so it cannot be picked up by the next page of that scan.
            await AsyncTest.UntilAsync(async () =>
            {
                await using var db = fixture.Database.Open();
                var contact = await db.Contacts.SingleAsync(Token);
                return contact.ConfirmedGrantToJson == contact.GrantToJson;
            });
        }

        Assert.Equal(1, (await manager.ImportBackupAsync(Replay(records), Token)).Applied);
        await using (var db = fixture.Database.Open())
        {
            var restored = await db.Contacts.SingleAsync(value => value.AccountId == restoredPeer.AccountId, Token);
            Assert.Equal(-1, restored.DeviceNotificationRevision);
            Assert.Null(restored.ConfirmedGrantToJson);
            Assert.False(await db.MessageOutbox.AnyAsync(value => value.Recipient == restoredPeer.AccountId, Token));
        }
        if (importWhileRunning)
            clock.Advance(TimeSpan.FromSeconds(15));
        else
            await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () =>
        {
            await using var db = fixture.Database.Open();
            var restored = await db.Contacts.SingleAsync(value => value.AccountId == restoredPeer.AccountId, Token);
            return restored.DeviceNotificationRevision == revision && restored.ConfirmedGrantToJson == restored.GrantToJson;
        });

        // Reimporting the same contact does not reset its completed maintenance.
        Assert.Equal(1, (await manager.ImportBackupAsync(Replay(records), Token)).Unchanged);
        clock.Advance(TimeSpan.FromSeconds(15));
        var now = clock.GetUtcNow();
        await AsyncTest.UntilAsync(async () => (await manager.GetSyncStatusAsync(fixture.Relay.RelayId, Token)).LastSynchronizedAt == now);
        await manager.StopAsync(Token);
        await using var check = fixture.Database.Open();
        var current = await check.Contacts.SingleAsync(value => value.AccountId == restoredPeer.AccountId, Token);
        Assert.Equal(revision, current.DeviceNotificationRevision);
        Assert.Equal(current.GrantToJson, current.ConfirmedGrantToJson);
        Assert.Equal(2, await check.MessageOutbox.CountAsync(value => value.Recipient == restoredPeer.AccountId, Token));
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
