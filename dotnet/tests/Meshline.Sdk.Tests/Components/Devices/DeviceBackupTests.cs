using Meshline.Models.Client;
using Meshline.Backups;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Tests.Components.Devices;

public sealed class DeviceBackupTests
{
    const string Password = "backup test password \U0001F510";
    static readonly BackupOptions WithHistory = new() { DirectHistory = BackupSelection.All, GroupHistory = BackupSelection.All };
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Newer_restored_device_state_uses_normal_cache_while_offline_and_rejects_older_refreshes(bool reopen, bool partial)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        ContactGrant grant;
        await using (var db = fixture.Database.Open())
            grant = ProtocolModel.FromJson<ContactGrant>((await db.Contacts.FindAsync([peer.AccountId], Token))!.GrantFromJson!)!;
        var original = await fixture.Client.DeviceManager.RefreshDeviceStateAsync(grant, Token);
        Assert.NotNull(original);
        await fixture.Client.StopAsync(Token);
        using var backup = new MemoryStream();
        await fixture.Client.ExportBackupAsync(backup, Password, WithHistory, cancellationToken: Token);
        var restored = original with
        {
            Revision = original.Revision + 1,
            AccountSignature = []
        };
        restored = restored with
        {
            AccountSignature = [.. peer.Sign(restored.GetSigningInput(TestNetwork.Context))]
        };
        using var document = JsonDocument.Parse(restored.ToJson());
        var bytes = await BackupFormatSetup.RewriteRecordsAsync(backup.ToArray(), Password, Token, (type, data) =>
            type == "device_state" && data.GetProperty("account").GetString() == peer.AccountId ? document.RootElement :
            partial && type == "end" ? JsonSerializer.SerializeToElement(new
            {
                records = -1
            }) : data);
        using var input = new MemoryStream(bytes);
        fixture.Relay.Handler = (request, _) => request.Method == "device.state.resolve" ? throw new HttpRequestException("offline") : Task.FromResult(fixture.Relay.Respond(request));
        var requests = fixture.Relay.Requests.Count;
        if (partial)
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.ImportBackupAsync(input, Password, Token));
        else
            await fixture.Client.ImportBackupAsync(input, Password, Token);
        Assert.Equal(requests, fixture.Relay.Requests.Count);
        if (reopen)
            await fixture.ReopenAsync();

        var manager = fixture.Client.DeviceManager;
        var queries = fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve");
        var cached = await manager.GetDeviceStateAsync(grant, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(restored.ToJson(), cached!.ToJson());
        if (!reopen)
            Assert.Equal(queries, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.RefreshDeviceStateAsync(grant, Token));
        Assert.Equal(restored.ToJson(), (await manager.GetDeviceStateAsync(grant, Token))!.ToJson());

        // The relay still holds the earlier revision. A failed refresh cannot roll back the imported state.
        fixture.Relay.Handler = null;
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.RefreshDeviceStateAsync(grant, Token));
        Assert.Equal(restored.ToJson(), (await manager.GetDeviceStateAsync(grant, Token))!.ToJson());
        await using var check = fixture.Database.Open();
        Assert.Equal(restored.ToJson(), (await check.DeviceStates.FindAsync([peer.AccountId], Token))!.DocumentJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_restore_preserves_refresh_deadline_and_new_caches_refresh_in_background(bool warmCache)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var grant = await PeerGrantAsync(fixture, peer);
        var manager = fixture.Client.DeviceManager;
        if (warmCache)
            Assert.NotNull(await manager.RefreshDeviceStateAsync(grant, Token));
        var queries = fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve");
        fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(30));
        var restored = fixture.Relay.Devices[peer.AccountId] with
        {
            Revision = 1,
            AccountSignature = []
        };
        restored = restored with
        {
            AccountSignature = [.. peer.Sign(restored.GetSigningInput(TestNetwork.Context))]
        };
        var record = BackupData.Record("device_state", BackupData.Element(restored.ToJson()));
        Assert.Equal(1, (await manager.ImportBackupAsync(Replay([record]), Token)).Applied);
        Assert.Equal(queries, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
        fixture.Relay.Devices[peer.AccountId] = restored;
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.resolve")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return fixture.Relay.Respond(request);
        };
        try
        {
            Assert.Equal(restored.ToJson(), (await manager.GetDeviceStateAsync(grant, Token).WaitAsync(TimeSpan.FromSeconds(5), Token))!.ToJson());
            if (warmCache)
            {
                Assert.Equal(queries, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
                fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));
                Assert.Equal(restored.ToJson(), (await manager.GetDeviceStateAsync(grant, Token))!.ToJson());
                Assert.Equal(queries, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
                fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
                Assert.Equal(restored.ToJson(), (await manager.GetDeviceStateAsync(grant, Token).WaitAsync(TimeSpan.FromSeconds(5), Token))!.ToJson());
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(queries + 1, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
            var joined = manager.RefreshDeviceStateAsync(grant, Token);
            release.TrySetResult();
            Assert.Equal(restored.ToJson(), (await joined)!.ToJson());
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task Device_restore_preserves_signature_and_grant_authorization_checks(string rejection)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var grant = await PeerGrantAsync(fixture, peer);
        var manager = fixture.Client.DeviceManager;
        var original = await manager.RefreshDeviceStateAsync(grant, Token);
        Assert.NotNull(original);
        var restored = original with
        {
            Revision = original.Revision + 1,
            AccountSignature = []
        };
        if (rejection == "revoked")
            restored = restored with
            {
                Certificates = []
            };
        restored = restored with
        {
            AccountSignature = [.. peer.Sign(restored.GetSigningInput(TestNetwork.Context))]
        };
        if (rejection == "signature")
            restored = restored with
            {
                AccountSignature = [.. new byte[64]]
            };
        var record = BackupData.Record("device_state", BackupData.Element(restored.ToJson()));
        if (rejection == "signature")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.ImportBackupAsync(Replay([record]), Token));
            Assert.Equal(original.ToJson(), (await manager.GetDeviceStateAsync(grant, Token))!.ToJson());
            await using var database = fixture.Database.Open();
            Assert.Equal(original.ToJson(), (await database.DeviceStates.FindAsync([peer.AccountId], Token))!.DocumentJson);
        }
        else
        {
            Assert.Equal(1, (await manager.ImportBackupAsync(Replay([record]), Token)).Applied);
            fixture.Relay.Devices[peer.AccountId] = restored;
            if (rejection == "expired")
            {
                fixture.Relay.Clock.Advance(TimeSpan.FromHours(4));
                await Assert.ThrowsAsync<ArgumentException>(() => manager.GetDeviceStateAsync(grant, Token));
            }
            else
                await Assert.ThrowsAsync<CryptographicException>(() => manager.GetDeviceStateAsync(grant, Token));
        }
    }

    static async Task<ContactGrant> PeerGrantAsync(TestClient fixture, AccountSigner peer)
    {
        var signer = fixture.Relay.AddPeer(peer);
        var grant = new ContactGrant
        {
            Grantor = peer.AccountId,
            Grantee = fixture.Account.AccountId,
            ExpiresAt = Clock.UtcNow.AddHours(4).ToUnixTimeSeconds(),
            Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
        };
        return grant with
        {
            Signatures = grant.Signatures.Add(signer.Certificate.GetDeviceId(TestNetwork.Context), [.. await signer.SignAsync(grant.GetSigningInput(TestNetwork.Context), Token)])
        };
    }

    [Fact]
    public async Task Conflicting_device_record_does_not_replace_the_live_verified_cache()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        await ContactSetup.AcceptPeerAsync(fixture, peer, Token);
        ContactGrant grant;
        await using (var db = fixture.Database.Open())
            grant = ProtocolModel.FromJson<ContactGrant>((await db.Contacts.FindAsync([peer.AccountId], Token))!.GrantFromJson!)!;
        var original = await fixture.Client.DeviceManager.GetDeviceStateAsync(grant, Token);
        Assert.NotNull(original);
        var conflict = fixture.Relay.Devices[peer.AccountId] with
        {
            Certificates = [],
            AccountSignature = []
        };
        conflict = conflict with
        {
            AccountSignature = [.. peer.Sign(conflict.GetSigningInput(TestNetwork.Context))]
        };
        var record = BackupData.Record("device_state", BackupData.Element(conflict.ToJson()));
        var report = await fixture.Client.DeviceManager.ImportBackupAsync(Replay([record]), Token);
        Assert.Equal(1, report.Conflicts);
        var requests = fixture.Relay.Requests.Count;
        var retained = await fixture.Client.DeviceManager.GetDeviceStateAsync(grant, Token);
        Assert.NotNull(retained);
        Assert.Equal(original.ToJson(), retained.ToJson());
        Assert.Equal(requests, fixture.Relay.Requests.Count);
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
            yield return record;
        await Task.CompletedTask;
    }
}
