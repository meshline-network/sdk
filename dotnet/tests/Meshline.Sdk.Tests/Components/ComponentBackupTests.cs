using Meshline.Backups;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Meshline.Tests.Components;

public sealed class ComponentBackupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Fact]
    public async Task Component_import_does_not_query_binding_or_local_device()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync(establish: false);
        var record = BackupData.Record("channel", new
        {
            channel_id = "chan_AAAAAAAAAAAAAAAAAAAAAA",
            relay_id = fixture.Relay.RelayId,
            is_followed = true
        });
        var bindingQueries = 0;
        var deviceQueries = 0;
        using var observer = new DatabaseObserver((name, value) =>
        {
            if (!name.EndsWith("CommandExecuted", StringComparison.Ordinal) || value is not CommandExecutedEventData command
                || command.Command.Connection?.DataSource != fixture.Database.Options.Path)
                return;
            var sql = command.Command.CommandText;
            if (sql.Contains("FROM \"DatabaseBinding\"", StringComparison.Ordinal))
                Interlocked.Increment(ref bindingQueries);
            if (sql.Contains("FROM \"LocalDevice\"", StringComparison.Ordinal))
                Interlocked.Increment(ref deviceQueries);
        });
        var report = await fixture.Client.ChannelManager.ImportBackupAsync(Replay(Enumerable.Repeat(record, 20)), Token);
        Assert.Equal(1, report.Applied);
        Assert.Equal(19, report.Unchanged);
        Assert.Equal(0, bindingQueries);
        Assert.Equal(0, deviceQueries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"data\":$DATA}")]
    [InlineData("{\"type\":null,\"data\":$DATA}")]
    [InlineData("{\"type\":1,\"data\":$DATA}")]
    [InlineData("{\"type\":\"\",\"data\":$DATA}")]
    [InlineData("{\"type\":\"channel\"}")]
    [InlineData("{\"type\":\"channel\",\"data\":null}")]
    [InlineData("{\"type\":\"channel\",\"data\":[]}")]
    [InlineData("{\"type\":\"message\",\"data\":$DATA}")]
    [InlineData("{\"type\":\"channel\",\"data\":$DATA,\"extra\":true}")]
    [InlineData("{\"type\":\"channel\",\"type\":\"channel\",\"data\":$DATA}")]
    [InlineData("{\"type\":\"channel\",\"data\":$DATA,\"data\":$DATA}")]
    public async Task Component_rejects_invalid_json_record_and_retains_prior_commit(string? invalid)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        var record = BackupData.Record("channel",
            JsonSerializer.SerializeToElement(new
            {
                channel_id = "chan_AAAAAAAAAAAAAAAAAAAAAA",
                relay_id = fixture.Relay.RelayId,
                is_followed = true
            }));
        var bad = invalid is null ? default : BackupData.Element(invalid.Replace("$DATA", record.GetProperty("data").GetRawText(), StringComparison.Ordinal));
        var requests = fixture.Relay.Requests.Count;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.ChannelManager.ImportBackupAsync(Replay([record, bad]), Token));
        await using var check = fixture.Database.Open();
        Assert.Single(await check.Channels.ToListAsync(Token));
        Assert.Equal(requests, fixture.Relay.Requests.Count);
        Assert.Equal(0, (await fixture.Client.ChannelManager.ImportBackupAsync(Replay([record]), Token)).Applied);
    }

    [Fact]
    public async Task Component_consumes_document_owned_records_before_advancing_and_exports_independent_elements()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        string[] ids = ["chan_AAAAAAAAAAAAAAAAAAAAAA", "chan_AAAAAAAAAAAAAAAAAAAAAQ"];
        var report = await fixture.Client.ChannelManager.ImportBackupAsync(Records(), Token);
        Assert.Equal(ids.Length, report.Applied);
        var exported = new List<JsonElement>();
        await foreach (var record in fixture.Client.ChannelManager.ExportBackupAsync(Token))
            exported.Add(record);
        await fixture.Client.ChannelManager.DisposeAsync();
        Assert.Equal(ids, exported.Select(record => record.GetProperty("data").GetProperty("channel_id").GetString()));

        async IAsyncEnumerable<JsonElement> Records()
        {
            foreach (var id in ids)
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    type = "channel",
                    data = new
                    {
                        channel_id = id,
                        relay_id = fixture.Relay.RelayId,
                        is_followed = true
                    }
                }));
                yield return document.RootElement;
                await using var check = fixture.Database.Open();
                Assert.True(await check.Channels.AnyAsync(row => row.ChannelId == id, Token));
            }
        }
    }

    [Fact]
    public async Task Component_disposal_cancels_input_enumeration_and_keeps_committed_records()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var peer = new AccountSigner();
        var record = BackupData.Record("contact",
            BackupData.Element(new ContactRecord { Account = peer.AccountId, Status = ContactRelationshipState.Deleted, UpdatedAt = 1 }.ToJson()));
        var entered = AsyncTest.Signal();
        var import = fixture.Client.MessageManager.ImportBackupAsync(Paused(Token), Token);
        if (await Task.WhenAny(entered.Task, import).WaitAsync(Token) == import)
            await import;
        await entered.Task.WaitAsync(Token);
        var disposal = fixture.Client.MessageManager.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import.WaitAsync(Token));
        await disposal.WaitAsync(Token);
        await using var check = fixture.Database.Open();
        Assert.Single(await check.Contacts.ToListAsync(Token));

        async IAsyncEnumerable<JsonElement> Paused([EnumeratorCancellation] CancellationToken token)
        {
            yield return record;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }

    static async IAsyncEnumerable<JsonElement> Replay(IEnumerable<JsonElement> records)
    {
        foreach (var record in records)
            yield return record;
        await Task.CompletedTask;
    }
}
