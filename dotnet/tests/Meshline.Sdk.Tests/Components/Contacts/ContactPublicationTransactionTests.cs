using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;

namespace Meshline.Tests.Components.Contacts;

public sealed class ContactPublicationTransactionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Contact_creation_during_account_signing_prevents_removal_without_saving_a_publication()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock).Certificate;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, remaining], cancellationToken: Token);
        await fixture.Client.DisposeAsync();
        var signer = new PausingSigner(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        fixture.Relay.Requests.Clear();
        var removal = client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
        ContactRequestInfo request;
        try
        {
            await signer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            request = await client.MessageManager.AddContactAsync(peer.AccountId, cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        finally { signer.Release.TrySetResult(); }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => removal);

        Assert.Contains("replacement_grant_not_confirmed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        await using (var verify = fixture.Database.Open())
        {
            Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
            Assert.Equal(request.MessageId, (await verify.ContactRequests.SingleAsync(Token)).MessageId);
        }
        // No barrier was committed: cancellation and the next removal can proceed.
        Assert.True(await client.MessageManager.CancelMessageAsync(request.MessageId!, Token));
        await client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
        Assert.DoesNotContain(client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
    }

    [Fact]
    public async Task State_refreshed_while_signing_forces_a_new_snapshot_and_preserves_another_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        await fixture.Client.DisposeAsync();
        var signer = new PausingSigner(fixture.Account);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options, fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        var removal = client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
        string concurrentId;
        try
        {
            await signer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using var database = new TestDatabase();
            await database.MigrateAsync();
            await using var pool = fixture.Relay.Pool(fixture.Account);
            await using var concurrent = new MeshlineClient(fixture.Relay.Options(fixture.Account), database.Options, pool, fixture.Protector, fixture.Account);
            await concurrent.InitializeAsync(Token);
            await concurrent.EstablishAccountAsync(cancellationToken: Token);
            concurrentId = concurrent.Device!.GetDeviceId(TestNetwork.Context);
            await client.DeviceManager.RefreshDeviceStateAsync(cancellationToken: Token);
            fixture.Relay.Requests.Clear();
        }
        finally { signer.Release.TrySetResult(); }

        await removal;

        Assert.Equal(2, signer.Publications);
        Assert.DoesNotContain(client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
        Assert.Contains(client.DeviceState.Certificates, value => value.GetDeviceId(TestNetwork.Context) == concurrentId);
        Assert.Single(fixture.Relay.Requests, value => value.Method == "device.state.publish");
    }

    [Fact]
    public async Task Failed_pending_save_rolls_back_the_barrier_and_retains_the_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        fixture.Relay.AddPeer(peer);
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock).Certificate;
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, remaining], cancellationToken: Token);
        var current = fixture.Client.Device!.ToJson();
        await using (var database = fixture.Database.Open())
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_pending BEFORE UPDATE ON SignedRequests WHEN NEW.Method = 'device.state.publish' AND NEW.Pending = 1 BEGIN SELECT RAISE(ABORT, 'pending save failed'); END;", Token);
        fixture.Relay.Requests.Clear();

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token));

        Assert.Equal(current, fixture.Client.Device!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method == "device.state.publish");
        await using (var database = fixture.Database.Open())
        {
            Assert.False((await database.SignedRequests.SingleAsync(value => value.Method == "device.state.publish", Token)).Pending);
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_pending;", Token);
        }
        var contact = await fixture.Client.MessageManager.AddContactAsync(peer.AccountId, cancellationToken: Token);
        Assert.True(await fixture.Client.MessageManager.CancelMessageAsync(contact.MessageId!, Token));
        await fixture.ReopenAsync();
        await fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
        Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
    }

    [Fact]
    public async Task A_large_contact_history_does_not_keep_a_write_transaction_open_during_submission()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var selectedId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        var remaining = new DeviceSigner(fixture.Account, fixture.Relay.Clock);
        await fixture.Client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, remaining.Certificate], cancellationToken: Token);
        await using (var database = fixture.Database.Open())
        {
            for (var index = 0; index < 1000; index++)
            {
                using var peer = new AccountSigner();
                var grant = new ContactGrant
                {
                    Grantor = fixture.Account.AccountId,
                    Grantee = peer.AccountId,
                    ExpiresAt = Clock.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                    Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                };
                var input = grant.GetSigningInput(TestNetwork.Context);
                grant = grant with
                {
                    Signatures = grant.Signatures
                    .Add(selectedId, [.. await fixture.Client.DeviceManager.SignAsync(input, Token)])
                    .Add(remaining.Certificate.GetDeviceId(TestNetwork.Context), [.. await remaining.SignAsync(input, Token)])
                };
                database.Contacts.Add(new()
                {
                    AccountId = peer.AccountId,
                    State = ContactRelationshipState.Active,
                    GrantToJson = grant.ToJson(),
                    ConfirmedGrantToJson = grant.ToJson(),
                    UpdatedAt = Clock.UtcNow
                });
            }
            await database.SaveChangesAsync(Token);
        }
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        var elapsed = Stopwatch.StartNew();
        var removal = fixture.Client.DeviceManager.RemoveDeviceAsync(selectedId, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            TestContext.Current.TestOutputHelper!.WriteLine($"1000-contact removal checks and durable publication preparation: {elapsed.Elapsed.TotalMilliseconds:F0} ms");
            var message = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId,
                new() { Body = new() { ContentType = "text/plain", Text = "during publication" } }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(MessageSendState.Queued, message.State);
        }
        finally { release.TrySetResult(); }
        await removal;
        Assert.DoesNotContain(fixture.Client.DeviceState!.Certificates, value => value.GetDeviceId(TestNetwork.Context) == selectedId);
    }

    sealed class PausingSigner(AccountSigner inner) : IAccountSigner
    {
        public string AccountId => inner.AccountId;
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();
        public int Publications { get; private set; }

        public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetString(data.Span).Contains("meshline.account.device.state", StringComparison.Ordinal) && ++Publications == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.SignAsync(data, cancellationToken);
        }
    }
}
