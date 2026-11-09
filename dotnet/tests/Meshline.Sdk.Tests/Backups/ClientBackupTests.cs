using Meshline.Backups;
using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Tests.Backups;

public sealed class ClientBackupTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "backup test password 🔐";
    static readonly BackupOptions WithHistory = new() { DirectHistory = BackupSelection.All, GroupHistory = BackupSelection.All };
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Fact]
    public async Task Contact_only_restore_refreshes_an_existing_conversation_through_contact_changed()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        await AddHistoryAsync(source, peer.AccountId);
        var changes = new ConcurrentQueue<ConversationChangedEventArgs>();
        target.Client.ConversationChanged += (_, change) => changes.Enqueue(change);
        await target.Client.MessageManager.ImportBackupAsync(source.Client.MessageManager.ExportBackupAsync(BackupSelection.All, Token), Token);
        await AsyncTest.UntilAsync(() => changes.Any(value => value.ChangeKind == ConversationChangeKind.Created));
        var messages = 0;
        target.Client.MessageManager.MessageReceived += (_, _) => messages++;
        await target.Client.MessageManager.ImportBackupAsync(Contact(), Token);
        await AsyncTest.UntilAsync(() => changes.Any(value => value.ChangeKind == ConversationChangeKind.Updated));
        Assert.All(changes, change => Assert.Equal(peer.AccountId, change.ConversationId));
        Assert.Equal("restored alias", (await target.Client.MessageManager.GetContactAsync(peer.AccountId, Token))!.Alias);
        Assert.Equal(0, messages);

        async IAsyncEnumerable<JsonElement> Contact()
        {
            yield return BackupData.Record("contact", BackupData.Element(new ContactRecord
            {
                Account = peer.AccountId,
                Status = ContactRelationshipState.Active,
                Alias = "restored alias",
                UpdatedAt = Clock.UtcNow.ToUnixTimeSeconds()
            }.ToJson()));
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Running_file_export_opens_component_snapshots_when_their_streams_are_read()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        using var later = new AccountSigner();
        await AddHistoryAsync(source, peer.AccountId);
        await source.Client.StartAsync(Token);
        using var output = new PausedWriteStream();
        var export = source.Client.ExportBackupAsync(output, Password, WithHistory, cancellationToken: Token);
        await output.Entered.Task.WaitAsync(Token);
        try
        {
            // Writing the container header does not establish component snapshots. A writer can still finish.
            await AddHistoryAsync(source, later.AccountId);
            Assert.Equal(ComponentState.Running, source.Client.LifecycleState);
        }
        finally { output.Release.TrySetResult(); }
        await export;
        await source.Client.StopAsync(Token);
        output.Position = 0;
        await target.Client.ImportBackupAsync(output, Password, Token);
        Assert.NotNull(await target.Client.GetConversationAsync(peer.AccountId, Token));
        Assert.NotNull(await target.Client.GetConversationAsync(later.AccountId, Token));
        Assert.NotNull(await source.Client.GetConversationAsync(later.AccountId, Token));
    }

    [Fact]
    public async Task Import_refreshes_conversations_and_notifies_committed_history_without_network_work()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        await AddHistoryAsync(source, peer.AccountId);
        using var backup = await ExportAsync(source);
        await target.Client.StopAsync(Token);
        await using var oldReader = await target.Client.MessageManager.GetMessageHistoryAsync(peer.AccountId, Token);
        var changes = new ConcurrentQueue<ConversationChangedEventArgs>();
        var messages = 0;
        target.Client.ConversationChanged += (_, change) => changes.Enqueue(change);
        target.Client.MessageManager.MessageReceived += (_, _) => Interlocked.Increment(ref messages);
        var requests = target.Relay.Requests.Count;
        await target.Client.ImportBackupAsync(backup, Password, Token);
        await AsyncTest.UntilAsync(() => changes.Count == 1);
        Assert.Equal(ConversationChangeKind.Created, changes.Single().ChangeKind);
        Assert.Equal(peer.AccountId, changes.Single().ConversationId);
        Assert.Equal(1, messages);
        Assert.Equal(requests, target.Relay.Requests.Count);
        Assert.Equal(ComponentState.Stopped, target.Client.LifecycleState);
        Assert.Empty(await oldReader.ReadNextAsync(10, Token));
        await using var fresh = await target.Client.MessageManager.GetMessageHistoryAsync(peer.AccountId, Token);
        Assert.Single(await fresh.ReadNextAsync(10, Token));
        Assert.Equal(1, (await target.Client.GetConversationAsync(peer.AccountId, Token))!.UnreadCount);
        backup.Position = 0;
        await target.Client.ImportBackupAsync(backup, Password, Token);
        Assert.Single(changes); // A duplicate import does not announce unchanged data.
        Assert.Equal(1, messages);
        await target.Client.StartAsync(Token);
        await target.Client.StopAsync(Token);
        Assert.NotNull(await target.Client.GetConversationAsync(peer.AccountId, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_merges_while_client_or_independently_started_child_is_running(bool child)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var backup = await ExportAsync(fixture);
        ClientComponent running = child ? fixture.Client.MessageManager : fixture.Client;
        await running.StartAsync(Token);
        await fixture.Client.ImportBackupAsync(backup, Password, Token);
        Assert.Equal(ComponentState.Running, running.LifecycleState);
        await fixture.Client.StopAsync(Token);
        backup.Position = 0;
        await fixture.Client.ImportBackupAsync(backup, Password, Token);
    }

    [Fact]
    public async Task Import_allows_unrelated_foreground_work_without_interrupting_it()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var backup = await ExportAsync(fixture);
        await fixture.Client.StopAsync(Token);
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
        var refresh = fixture.Client.DeviceManager.RefreshDeviceStateAsync(cancellationToken: Token);
        await entered.Task.WaitAsync(Token);
        try
        {
            await fixture.Client.ImportBackupAsync(backup, Password, Token);
            Assert.False(refresh.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await refresh;
        backup.Position = 0;
        await fixture.Client.ImportBackupAsync(backup, Password, Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_allows_local_operations_and_start_or_stop(bool start)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = Create();
        await fixture.InitializeAsync();
        using var output = await ExportAsync(fixture);
        await fixture.Client.StopAsync(Token);
        using var input = new PausedReadStream(output.ToArray());
        var import = fixture.Client.ImportBackupAsync(input, Password, Token);
        await input.Entered.Task.WaitAsync(Token);
        Task transition;
        try
        {
            var client = fixture.Client;
            await client.MarkReadAsync(fixture.Account.AccountId, Token);
            await client.MessageManager.GetContactAsync(fixture.Account.AccountId, Token);
            await using var groups = await client.GroupManager.GetGroupsAsync(cancellationToken: Token);
            await using var channels = await client.ChannelManager.GetFollowedAsync(cancellationToken: Token);
            using var attempted = new MemoryStream();
            await client.ExportBackupAsync(attempted, Password, WithHistory, cancellationToken: Token);
            transition = start ? client.StartAsync(Token) : client.StopAsync(Token);
            await transition.WaitAsync(Token);
        }
        finally { input.Release.TrySetResult(); }
        await import;
        await transition;
        Assert.Equal(start ? ComponentState.Running : ComponentState.Stopped, fixture.Client.LifecycleState);
        await fixture.Client.StopAsync(Token);
    }

    [Fact]
    public async Task Disposing_client_cancels_container_read_without_deadlock()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        await AddHistoryAsync(source, peer.AccountId);
        using var output = await ExportAsync(source);
        using var input = new PausedReadStream(output.ToArray());
        var import = target.Client.ImportBackupAsync(input, Password, Token);
        await input.Entered.Task.WaitAsync(Token);
        ClientComponent disposed = target.Client;
        var disposal = disposed.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import.WaitAsync(Token));
        await disposal.WaitAsync(Token);
        await using var db = target.Database.Open();
        Assert.Empty(await db.Messages.ToListAsync(Token));
        Assert.Equal(ComponentState.Disposed, disposed.LifecycleState);
    }

    [Theory]
    [InlineData("cancellation")]
    [InlineData("authentication")]
    [InlineData("storage")]
    public async Task Failed_first_record_import_allows_the_same_client_to_retry(string failure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        await AddHistoryAsync(source, peer.AccountId);
        using var output = await ExportAsync(source);
        var changes = new ConcurrentQueue<ConversationChangedEventArgs>();
        target.Client.ConversationChanged += (_, change) => changes.Enqueue(change);
        var bytes = output.ToArray();
        if (failure == "authentication")
            bytes[^1] ^= 1;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var input = new PausedReadStream(bytes);
        await using (var trigger = failure == "storage" ? await target.Database.CreateTriggerAsync("fail_import", "BEFORE INSERT ON Messages BEGIN SELECT RAISE(ABORT, 'injected failure'); END") : null)
        {
            var import = target.Client.ImportBackupAsync(input, Password, cancellation.Token);
            if (failure != "storage")
                await input.Entered.Task.WaitAsync(Token);
            if (failure == "cancellation")
                cancellation.Cancel();
            input.Release.TrySetResult();
            switch (failure)
            {
                case "cancellation":
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
                    break;
                case "authentication":
                    await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => import);
                    break;
                case "storage":
                    await Assert.ThrowsAsync<DbUpdateException>(() => import);
                    break;
                default:
                    await Assert.ThrowsAsync<InvalidDataException>(() => import);
                    break;
            }
        }
        Assert.Equal(ComponentState.Stopped, target.Client.LifecycleState);
        Assert.Null(await target.Client.GetConversationAsync(peer.AccountId, Token));
        Assert.Empty(changes);
        await using (var db = target.Database.Open())
        {
            Assert.Empty(await db.Messages.ToListAsync(Token));

        }
        output.Position = 0;
        await target.Client.ImportBackupAsync(output, Password, Token);
        Assert.NotNull(await target.Client.GetConversationAsync(peer.AccountId, Token));
    }

    static async Task<MemoryStream> ExportAsync(TestClient fixture)
    {
        var stream = new MemoryStream();
        await fixture.Client.ExportBackupAsync(stream, Password, WithHistory, cancellationToken: Token);
        stream.Position = 0;
        return stream;
    }

    static async Task AddHistoryAsync(TestClient fixture, string sender)
    {
        await using var db = fixture.Database.Open();
        db.Messages.Add(new StoredMessageRecord
        {
            Sender = sender,
            Recipient = fixture.Account.AccountId,
            SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
            MessageId = Identifiers.CreateMessageId(),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(Clock.UtcNow.ToUnixTimeSeconds()),
            IsDirect = true,
            PayloadType = "meshline.message.direct",
            PayloadJson = new DirectMessage { Body = new() { ContentType = "text/plain", Text = "history" } }.ToJson()
        });
        await db.SaveChangesAsync(Token);
    }

    sealed class PausedWriteStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    sealed class PausedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = AsyncTest.Signal();
        public TaskCompletionSource Release { get; } = AsyncTest.Signal();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Pause the physical EOF probe used to authenticate the final encryption segment.
            if (Position > Length - 80)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
