using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Tests.Backups;

public sealed class BackupDependencyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "dependency check";
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Fact]
    public async Task Export_rejects_a_peer_device_change_between_snapshots_and_retry_restores_the_new_grant()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await source.InitializeAsync();
        await using var target = Create();
        await target.InitializeAsync();
        await using var peer = new TestClient();
        await peer.InitializeAsync();
        await ContactSetup.AcceptPeerAsync(source, peer.Account, Token);
        await target.Client.DeviceManager.ImportBackupAsync(source.Client.DeviceManager.ExportBackupAsync(Token), Token);
        await target.Client.MessageManager.ImportBackupAsync(source.Client.MessageManager.ExportBackupAsync(cancellationToken: Token), Token);
        var original = BackupData.Protocol<ContactRecord>((await source.Client.MessageManager.ExportBackupAsync(cancellationToken: Token)
            .ToListAsync(Token)).Single().GetProperty("data"));
        var oldGrant = original.GrantFromContact!;
        var replacementId = peer.Client.Device!.GetDeviceId(TestNetwork.Context);
        Assert.DoesNotContain(replacementId, oldGrant.Signatures.Keys);
        var state = peer.Client.DeviceState! with
        {
            Revision = 1
        };
        state = state with
        {
            AccountSignature = [.. peer.Account.Sign(state.GetSigningInput(TestNetwork.Context))]
        };
        var newGrant = oldGrant with
        {
            Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(replacementId,
                [.. await peer.Client.DeviceManager.SignAsync(oldGrant.GetSigningInput(TestNetwork.Context), Token)])
        };
        using var abandoned = new MemoryStream();
        var error = await ExportWithChangeAsync(source, abandoned, "Contacts", async () =>
        {
            await source.Client.DeviceManager.ImportBackupAsync(Records("device_state", state), Token);
            await source.Client.MessageManager.ImportBackupAsync(Records("contact", original with
            {
                GrantFromContact = newGrant,
                UpdatedAt = original.UpdatedAt + 1,
                Alias = "after device change"
            }), Token);
        });
        Assert.IsType<IOException>(error);
        abandoned.Position = 0;
        Assert.NotNull(await Record.ExceptionAsync(() => target.Client.ImportBackupAsync(abandoned, Password, Token)));

        using var retry = new MemoryStream();
        await source.Client.ExportBackupAsync(retry, Password, cancellationToken: Token);
        retry.Position = 0;
        Assert.Equal(0, (await target.Client.ImportBackupAsync(retry, Password, Token)).Conflicts);
        target.Relay.AddPeer(peer.Account);
        target.Relay.Devices[peer.Account.AccountId] = state;
        await target.Client.DeviceManager.RefreshDeviceStateAsync(peer.Account.AccountId, Token);
        var contact = await target.Client.MessageManager.GetContactAsync(peer.Account.AccountId, Token);
        Assert.Equal("after device change", contact!.Alias);
        Assert.Equal(ContactGrantState.Valid, contact.GrantFromContact);
        await target.Client.MessageManager.SendMessageAsync(peer.Account.AccountId,
            new()
            {
                Body = new()
                {
                    ContentType = "text/plain",
                    Text = "restored"
                }
            }, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_rejects_a_new_contact_whose_available_device_state_was_not_exported(bool alreadyCached)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await source.InitializeAsync();
        using var peer = new AccountSigner();
        if (alreadyCached)
        {
            source.Relay.AddPeer(peer);
            await source.Client.DeviceManager.RefreshDeviceStateAsync(peer.AccountId, Token);
        }
        using var output = new MemoryStream();
        var error = await ExportWithChangeAsync(source, output, "Contacts",
            () => ContactSetup.AcceptPeerAsync(source, peer, Token));
        Assert.IsType<IOException>(error);
        using var retry = new MemoryStream();
        await source.Client.ExportBackupAsync(retry, Password, cancellationToken: Token);
        retry.Position = 0;
        await using var codec = await BackupCodec.OpenAsync(retry, Password, Token);
        var states = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var frame = BackupData.ReadRecord(await codec.ReadAsync(Token));
            if (frame.Type == "end")
                break;
            if (frame.Type == "device_state")
                states.Add(BackupData.Protocol<AccountDeviceState>(frame.Data).Account);
            if (frame.Type == "contact")
                Assert.Contains(BackupData.Protocol<ContactRecord>(frame.Data).Account, states);
        }
        Assert.Contains(peer.AccountId, states);
        await codec.RequireEndAsync(Token);
    }

    [Theory]
    [InlineData("late_contact")]
    [InlineData("deleted_contact")]
    [InlineData("alias")]
    public async Task Export_allows_changes_that_do_not_change_its_device_dependencies(string change)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        await using var source = Create();
        await source.InitializeAsync();
        await using var target = Create();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        if (change != "late_contact")
            await ContactSetup.AcceptPeerAsync(source, peer, Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var output = new MemoryStream();
        var error = await ExportWithChangeAsync(source, output, change == "late_contact" ? "Groups" : "Contacts", async () =>
        {
            if (change == "late_contact")
                await ContactSetup.AcceptPeerAsync(source, peer, Token);
            else if (change == "alias")
                await source.Client.MessageManager.SetAliasAsync(peer.AccountId, "renamed", Token);
            else
            {
                await source.Client.MessageManager.ImportBackupAsync(Records("contact", new ContactRecord
                {
                    Account = peer.AccountId,
                    Status = ContactRelationshipState.Deleted,
                    UpdatedAt = clock.GetUtcNow().ToUnixTimeSeconds()
                }), Token);
                var state = source.Relay.Devices[peer.AccountId] with
                {
                    Revision = 1,
                    Certificates = []
                };
                state = state with
                {
                    AccountSignature = [.. peer.Sign(state.GetSigningInput(TestNetwork.Context))]
                };
                await source.Client.DeviceManager.ImportBackupAsync(Records("device_state", state), Token);
            }
        });
        Assert.Null(error);
        output.Position = 0;
        Assert.Equal(0, (await target.Client.ImportBackupAsync(output, Password, Token)).Conflicts);
        var contact = await target.Client.MessageManager.GetContactAsync(peer.AccountId, Token);
        if (change == "alias")
            Assert.Equal("renamed", contact!.Alias);
        else
            Assert.Null(contact);
    }

    static async Task<Exception?> ExportWithChangeAsync(TestClient fixture, Stream output, string table, Func<Task> change)
    {
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var paused = 0;
        using var observer = new DatabaseObserver((name, value) =>
        {
            if (!name.EndsWith("CommandExecuting", StringComparison.Ordinal) || value is not CommandEventData command
                || command.Command.Connection?.DataSource != fixture.Database.Options.Path
                || !command.Command.CommandText.Contains($"\nFROM \"{table}\"", StringComparison.Ordinal)
                || Interlocked.CompareExchange(ref paused, 1, 0) != 0)
                return;
            entered.TrySetResult();
            release.Task.WaitAsync(Token).GetAwaiter().GetResult();
        });
        // Pause before the component's first SELECT establishes its snapshot, after device export has finished.
        var export = Task.Run(() => fixture.Client.ExportBackupAsync(output, Password, cancellationToken: Token), Token);
        Exception? error;
        try
        {
            await Task.WhenAny(entered.Task, export).WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.True(entered.Task.IsCompletedSuccessfully);
            await change();
        }
        finally
        {
            release.TrySetResult();
            error = await Record.ExceptionAsync(() => export);
        }
        return error;
    }

    static async IAsyncEnumerable<JsonElement> Records(string type, ProtocolModel value)
    {
        yield return BackupData.Record(type, BackupData.Element(value.ToJson()));
        await Task.CompletedTask;
    }
}
