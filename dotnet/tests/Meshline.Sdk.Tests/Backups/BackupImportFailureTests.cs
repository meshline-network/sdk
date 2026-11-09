using Meshline.Backups;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Tests.Backups;

public sealed class BackupImportFailureTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "backup test password \U0001F510";
    static readonly BackupOptions WithHistory = new() { DirectHistory = BackupSelection.All, GroupHistory = BackupSelection.All };
    static TestClient Create() => new(new AccountSigner([.. new byte[31], 1]));

    [Theory]
    [InlineData("password")]
    [InlineData("account")]
    [InlineData("network")]
    [InlineData("tag")]
    [InlineData("truncate")]
    [InlineData("append")]
    [InlineData("version")]
    public async Task Invalid_single_segment_backup_does_not_commit_records(string damage)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        await using (var db = source.Database.Open())
        {
            db.Contacts.Add(new()
            {
                AccountId = peer.AccountId,
                Alias = "unauthenticated contact",
                UpdatedAt = Clock.UtcNow,
                State = ContactRelationshipState.Deleted
            });
            await db.SaveChangesAsync(Token);
        }
        await source.Client.StopAsync(Token);
        await target.Client.StopAsync(Token);
        using var output = new MemoryStream();
        await source.Client.ExportBackupAsync(output, Password, WithHistory, cancellationToken: Token);
        var bytes = output.ToArray();
        var password = Password;
        if (damage == "password")
            password += "wrong";
        else if (damage == "tag")
            bytes[^1] ^= 1;
        else if (damage == "truncate")
            bytes = bytes[..^1];
        else if (damage == "append")
            bytes = [.. bytes, 0];
        else if (damage == "version")
            bytes[7] = (byte)'9';
        else if (damage == "account")
            bytes = await BackupFormatSetup.RewriteRecordsAsync(bytes, Password, Token, (type, data) => type == "manifest"
            ? JsonSerializer.SerializeToElement(new
            {
                network = TestNetwork.Context.ToString(),
                account = peer.AccountId,
                created_at = 0,
                direct_history = new
                {
                    mode = "all",
                    ids = Array.Empty<string>()
                },
                group_history = new
                {
                    mode = "all",
                    ids = Array.Empty<string>()
                }
            }) : data);
        else if (damage == "network")
        {
            // Change the authenticated manifest while preserving the container's validity.
            bytes = await BackupFormatSetup.RewriteRecordsAsync(bytes, Password, Token, (type, data) => type == "manifest"
                ? JsonSerializer.SerializeToElement(new
                {
                    network = "another-network",
                    account = target.Account.AccountId,
                    created_at = 0,
                    direct_history = new
                    {
                        mode = "all",
                        ids = Array.Empty<string>()
                    },
                    group_history = new
                    {
                        mode = "all",
                        ids = Array.Empty<string>()
                    }
                }) : data);
        }
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAnyAsync<Exception>(() => target.Client.ImportBackupAsync(input, password, Token));
        await using var check = target.Database.Open();
        Assert.Empty(await check.Contacts.ToListAsync(Token));
        Assert.Empty(await check.Messages.ToListAsync(Token));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("network")]
    public async Task Manifest_is_compared_with_database_binding_before_reading_component_records(string mismatch)
    {
        using var time = Clock.Use(new ManualClock());
        await using var target = Create();
        await target.InitializeAsync();
        using var input = new MemoryStream();
        await using (var writer = await BackupCodec.CreateAsync(input, Password, Token))
        {
            await writer.WriteAsync("manifest", new BackupData.Manifest(TestNetwork.Context.ToString(), target.Account.AccountId, 0,
                new("none", []), new("none", [])), Token);
            // An authenticated manifest without subsequent records proves that binding is checked before reading them.
            await writer.CompleteAsync(Token);
        }
        await using (var db = target.Database.Open())
        {
            var binding = await db.Bindings.SingleAsync(Token);
            if (mismatch == "account")
                binding.AccountId = "another-account";
            else
                binding.Context = "another-network";
            await db.SaveChangesAsync(Token);
        }
        input.Position = 0;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => target.Client.ImportBackupAsync(input, Password, Token));
        Assert.Equal("The backup belongs to another account/network or has an invalid timestamp.", error.Message);
        await using var check = target.Database.Open();
        Assert.Empty(await check.Contacts.ToListAsync(Token));
        Assert.Empty(await check.Messages.ToListAsync(Token));
    }

    [Theory]
    [InlineData("record_count")]
    [InlineData("missing_end")]
    [InlineData("cancellation")]
    public async Task Failure_after_a_contact_insert_retains_only_committed_records(string failure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        await using (var db = source.Database.Open())
        {
            db.Contacts.Add(new()
            {
                AccountId = peer.AccountId,
                Alias = "pending transaction",
                UpdatedAt = Clock.UtcNow,
                State = ContactRelationshipState.Deleted
            });
            await db.SaveChangesAsync(Token);
        }
        using var original = new MemoryStream();
        await source.Client.ExportBackupAsync(original, Password, WithHistory, cancellationToken: Token);
        var bytes = original.ToArray();
        if (failure == "missing_end")
        {
            var compressed = await BackupFormatSetup.DecryptAsync(bytes, Password, Token);
            using var input = new MemoryStream(compressed);
            using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var plaintext = new MemoryStream();
            await gzip.CopyToAsync(plaintext, Token);
            var records = plaintext.ToArray();
            var offset = 0;
            var last = 0;
            while (offset < records.Length)
            {
                last = offset;
                offset += 4 + BinaryPrimitives.ReadInt32BigEndian(records.AsSpan(offset, 4));
            }
            compressed = await BackupFormatSetup.CompressAsync(records[..last], Token);
            bytes = await BackupFormatSetup.EncryptAsync(compressed, Password, Token);
        }
        else if (failure == "record_count")
            bytes = await BackupFormatSetup.RewriteRecordsAsync(bytes, Password, Token, (type, data) => type == "end" ? JsonSerializer.SerializeToElement(new { records = -1 }) : data);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var inserted = 0;
        using var observer = new DatabaseObserver((name, value) =>
        {
            if (name.EndsWith("CommandExecuted", StringComparison.Ordinal) && value is CommandExecutedEventData executed
                && executed.Command.Connection?.DataSource == target.Database.Options.Path
                && executed.Command.CommandText.StartsWith("INSERT INTO \"Contacts\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref inserted);
                if (failure == "cancellation")
                    cancellation.Cancel();
            }
        });
        using var backup = new MemoryStream(bytes);
        if (failure == "cancellation")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Client.ImportBackupAsync(backup, Password, cancellation.Token));
        else if (failure == "missing_end")
            await Assert.ThrowsAsync<EndOfStreamException>(() => target.Client.ImportBackupAsync(backup, Password, Token));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => target.Client.ImportBackupAsync(backup, Password, Token));
        Assert.Equal(1, inserted);
        await using var check = target.Database.Open();
        if (failure == "cancellation")
            Assert.Empty(await check.Contacts.ToListAsync(Token));
        else
            Assert.Single(await check.Contacts.ToListAsync(Token));
    }

    [Fact]
    public async Task Final_segment_authentication_failure_retains_previously_imported_history()
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        var random = new Random(1729);
        await using (var db = source.Database.Open())
        {
            for (var i = 0; i < 192; i++)
            {
                var payload = new byte[8192];
                random.NextBytes(payload);
                db.Messages.Add(Message(source, peer.AccountId, Convert.ToBase64String(payload)));
            }
            await db.SaveChangesAsync(Token);
        }
        using var original = new MemoryStream();
        await source.Client.ExportBackupAsync(original, Password, WithHistory, cancellationToken: Token);
        Assert.True(original.Length > BackupEncryptionStream.SegmentSize + 1000);
        var bytes = original.ToArray();
        bytes[^1] ^= 1;
        var inserted = 0;
        using (var observer = new DatabaseObserver((name, value) =>
        {
            if (name.EndsWith("CommandExecuted", StringComparison.Ordinal) && value is CommandExecutedEventData executed
                && executed.Command.Connection?.DataSource == target.Database.Options.Path
                && executed.Command.CommandText.StartsWith("INSERT INTO \"Messages\"", StringComparison.Ordinal))
                Interlocked.Increment(ref inserted);
        }))
        {
            using var input = new MemoryStream(bytes);
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => target.Client.ImportBackupAsync(input, Password, Token));
        }
        Assert.True(inserted > 0);
        await using (var check = target.Database.Open())
            Assert.Equal(inserted, await check.Messages.CountAsync(Token));
        original.Position = 0;
        await target.Client.ImportBackupAsync(original, Password, Token);
        await using var restored = target.Database.Open();
        Assert.Equal(192, await restored.Messages.CountAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_retains_earlier_records_when_storage_or_cancellation_fails(bool cancel)
    {
        using var time = Clock.Use(new ManualClock());
        await using var source = Create();
        await using var target = Create();
        await source.InitializeAsync();
        await target.InitializeAsync();
        using var peer = new AccountSigner();
        await using (var db = source.Database.Open())
        {
            db.Contacts.Add(new()
            {
                AccountId = peer.AccountId,
                UpdatedAt = Clock.UtcNow,
                State = ContactRelationshipState.Deleted
            });
            db.Channels.Add(new()
            {
                ChannelId = "chan_AAAAAAAAAAAAAAAAAAAAAA",
                RelayId = source.Relay.RelayId
            });
            await db.SaveChangesAsync(Token);
        }
        await source.Client.StopAsync(Token);
        await target.Client.StopAsync(Token);
        using var output = new MemoryStream();
        await source.Client.ExportBackupAsync(output, Password, WithHistory, cancellationToken: Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var input = new CancelNearEndStream(output.ToArray(), cancel ? cancellation : null);
        await using var trigger = cancel ? null : await target.Database.CreateTriggerAsync("fail_backup", "BEFORE INSERT ON Channels BEGIN SELECT RAISE(ABORT, 'injected failure'); END");
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Client.ImportBackupAsync(input, Password, cancellation.Token));
        else
            await Assert.ThrowsAsync<DbUpdateException>(() => target.Client.ImportBackupAsync(input, Password, Token));
        await using var check = target.Database.Open();
        if (cancel)
            Assert.Empty(await check.Contacts.ToListAsync(Token));
        else
            Assert.Single(await check.Contacts.ToListAsync(Token));
        Assert.Empty(await check.Channels.ToListAsync(Token));
    }

    sealed class CancelNearEndStream(byte[] bytes, CancellationTokenSource? cancellation) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > Length - 80)
                cancellation?.Cancel();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    static StoredMessageRecord Message(TestClient fixture, string sender, string text) => new()
    {
        MessageId = Identifiers.CreateMessageId(),
        Sender = sender,
        Recipient = fixture.Account.AccountId,
        SenderDeviceId = "dev_AAAAAAAAAAAAAAAAAAAAAA",
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(Clock.UtcNow.ToUnixTimeSeconds()),
        IsDirect = true,
        PayloadType = "meshline.message.direct",
        PayloadJson = new DirectMessage { Body = new() { ContentType = "text/plain", Text = text } }.ToJson()
    };
}
