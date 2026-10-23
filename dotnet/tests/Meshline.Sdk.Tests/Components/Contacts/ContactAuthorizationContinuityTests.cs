using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Contacts;

public sealed class ContactAuthorizationContinuityTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_and_replacement_reject_devices_without_local_issuance_evidence(bool replace)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var selected = new DeviceSigner(fixture.Account, new ManualClock(fixture.Relay.Clock.GetUtcNow().AddDays(-29))).Certificate;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, selected], cancellationToken: Token);
        var selectedId = selected.GetDeviceId(TestNetwork.Context);
        var original = fixture.Relay.Devices[fixture.Account.AccountId].ToJson();
        fixture.Relay.Requests.Clear();

        await AssertBlockedAsync(fixture, selectedId, replace, "contact_history_unavailable");

        Assert.Equal(original, fixture.Relay.Devices[fixture.Account.AccountId].ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("different")]
    [InlineData("invalid")]
    [InlineData("confirmed")]
    public async Task Removal_requires_confirmation_of_the_current_grant_by_a_remaining_device(string evidence)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        var remainingId = remaining.Certificate.GetDeviceId(TestNetwork.Context);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, remaining.Certificate], cancellationToken: Token);
        await using (var db = fixture.Database.Open())
        {
            var contact = await db.Contacts.SingleAsync(Token);
            var issued = ProtocolModel.FromJson<ContactGrant>(contact.GrantToJson!)!;
            issued = issued with { Signatures = issued.Signatures.Add(remainingId, [.. await remaining.SignAsync(issued.GetSigningInput(TestNetwork.Context), Token)]) };
            contact.GrantToJson = issued.ToJson();
            // Model durable delivery evidence, including an older accepted grant.
            var confirmed = evidence switch
            {
                "expired" => issued with { ExpiresAt = fixture.Relay.Clock.GetUtcNow().AddSeconds(-1).ToUnixTimeSeconds() },
                "different" => issued with { ExpiresAt = fixture.Relay.Clock.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds() },
                _ => issued
            };
            var signature = evidence == "invalid" ? new byte[64] : await remaining.SignAsync(confirmed.GetSigningInput(TestNetwork.Context), Token);
            contact.ConfirmedGrantToJson = (confirmed with { Signatures = confirmed.Signatures.SetItem(remainingId, [.. signature]) }).ToJson();
            await db.SaveChangesAsync(Token);
        }
        var original = fixture.Relay.Devices[fixture.Account.AccountId].ToJson();
        fixture.Relay.Requests.Clear();

        if (evidence == "confirmed")
        {
            await RemoveAsync(fixture, selectedId, false);
            Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
            Assert.Contains(fixture.Client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == remainingId);
            Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        }
        else
        {
            await AssertBlockedAsync(fixture, selectedId, false, "replacement_grant_not_confirmed", peer.AccountId);
            Assert.Equal(original, fixture.Relay.Devices[fixture.Account.AccountId].ToJson());
            Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_devices_do_not_require_missing_history_to_be_removed_or_replaced(bool replace)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var selected = new DeviceSigner(fixture.Account, new ManualClock(fixture.Relay.Clock.GetUtcNow().AddDays(-29))).Certificate;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, selected], cancellationToken: Token);
        var selectedId = selected.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Clock.Advance(TimeSpan.FromDays(2));

        await RemoveAsync(fixture, selectedId, replace);

        Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
    }

    static async Task AssertBlockedAsync(TestClient fixture, string selectedId, bool replace, string code, string? contactId = null)
    {
        if (replace)
        {
            var previousLocalDeviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => RemoveAsync(fixture, selectedId, replace));
            Assert.Equal(AccountEstablishmentReason.ContactContinuityUnverifiable, error.Reason);
            var issue = Assert.Single(error.ContactIssues);
            Assert.Equal(code, issue.Code);
            Assert.Equal(contactId, issue.AccountId);
            Assert.NotNull(error.DeviceSelection);
            Assert.Equal(previousLocalDeviceId, error.DeviceSelection.PreviousLocalDeviceId);
            var current = fixture.Relay.Devices[fixture.Account.AccountId];
            Assert.Equal(current.Revision, error.DeviceSelection.Revision);
            Assert.Equal(current.Certificates.Select(value => value.ToJson()), error.DeviceSelection.Devices.Select(value => value.Certificate.ToJson()));
            Assert.Contains(error.DeviceSelection.Devices, value => value.DeviceId == selectedId);
            Assert.All(error.DeviceSelection.Devices, value => Assert.Equal(value.Certificate.GetDeviceId(TestNetwork.Context), value.DeviceId));
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoveAsync(fixture, selectedId, replace));
            Assert.Contains(code, error.Message, StringComparison.Ordinal);
        }
    }

    static Task RemoveAsync(TestClient fixture, string selectedId, bool replace)
    {
        if (!replace) return fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
        return fixture.Client.EstablishAccountAsync(new() { ReplaceDeviceId = selectedId }, Token);
    }

}
