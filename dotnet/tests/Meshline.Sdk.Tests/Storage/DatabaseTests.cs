using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Meshline.Tests.Storage;

public sealed class DatabaseTests
{
    [Fact]
    public async Task Construction_has_no_io_and_initialization_requires_explicit_migration()
    {
        await using var database = new TestDatabase();
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var signer = new AccountSigner();
        await using var pool = relay.Pool(signer);
        await using var client = new MeshlineClient(relay.Options(signer), database.Options, pool, new SecretProtector(), signer);

        Assert.False(File.Exists(database.Options.Path));
        Assert.Empty(relay.Requests);
        await Assert.ThrowsAsync<SqliteException>(() => client.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(database.Options.Path));

        await database.MigrateAsync();
        await database.MigrateAsync();
        await client.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, client.LifecycleState);
        Assert.Empty(relay.Requests);

        await using var db = database.Open();

        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await db.Bindings.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Initial_baseline_upgrade_preserves_keys_pending_publications_and_history()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = new TestDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(database.Options.Path)!);
        var local = new LocalDeviceRecord
        {
            DeviceId = "device",
            CertificateJson = "certificate",
            ProtectedSigningKey = [1, 2, 3],
            ProtectedEncryptionKey = [4, 5, 6]
        };
        var pending = new SignedRequestRecord
        {
            Method = "device.state.publish",
            RelayId = "relay",
            DocumentJson = "signed-publication",
            Revision = 7,
            Pending = true
        };
        var message = Message("sender", DateTimeOffset.UnixEpoch);
        await using (var baseline = new MeshlineDbContext(database.Options, SqliteOpenMode.ReadWriteCreate))
        {
            await baseline.GetService<IMigrator>().MigrateAsync("20260925135953_InitialCreate", token);
            // Seed the original versioned device schema and publication without an execution key.
            await baseline.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO LocalDevice (DeviceId, CertificateJson, ProtectedSigningKey, ProtectedEncryptionKey, Version) VALUES ({local.DeviceId}, {local.CertificateJson}, {local.ProtectedSigningKey}, {local.ProtectedEncryptionKey}, {3})", token);
            await baseline.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SignedRequests (Method, RelayId, DocumentJson, Revision, Pending) VALUES ({pending.Method}, {pending.RelayId}, {pending.DocumentJson}, {pending.Revision}, {pending.Pending})", token);
            baseline.Messages.Add(message);
            await baseline.SaveChangesAsync(token);
        }

        await MeshlineDatabase.MigrateAsync(database.Options, token);

        await using var upgraded = database.Open();
        var retainedDevice = await upgraded.LocalDevices.SingleAsync(token);
        Assert.Equal(local.DeviceId, retainedDevice.DeviceId);
        Assert.Equal(local.CertificateJson, retainedDevice.CertificateJson);
        Assert.Equal(local.ProtectedSigningKey, retainedDevice.ProtectedSigningKey);
        Assert.Equal(local.ProtectedEncryptionKey, retainedDevice.ProtectedEncryptionKey);
        var retainedRequest = await upgraded.SignedRequests.SingleAsync(token);
        Assert.Equal(pending.Method, retainedRequest.Method);
        Assert.Equal(pending.RelayId, retainedRequest.RelayId);
        Assert.Equal(pending.DocumentJson, retainedRequest.DocumentJson);
        Assert.Equal(pending.Revision, retainedRequest.Revision);
        Assert.True(retainedRequest.Pending);
        Assert.Null(retainedRequest.OperationKey);
        var retainedMessage = await upgraded.Messages.SingleAsync(token);
        Assert.Equal(message.Sender, retainedMessage.Sender);
        Assert.Equal(message.MessageId, retainedMessage.MessageId);
        Assert.Equal(message.CreatedAt, retainedMessage.CreatedAt);
        Assert.Empty(await upgraded.AccountEstablishments.ToListAsync(token));
        Assert.Empty(await upgraded.DevicePreparations.ToListAsync(token));
        Assert.Equal(upgraded.Database.GetMigrations(), await upgraded.Database.GetAppliedMigrationsAsync(token));
    }

    [Fact]
    public async Task Gateway_intent_migration_preserves_unfinished_setup_draft_and_publication()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = new TestDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(database.Options.Path)!);
        var operationId = Guid.NewGuid();
        var draft = new DevicePreparationRecord
        {
            OperationId = operationId,
            DraftCertificateJson = "retained-draft",
            ProtectedSigningKey = [1, 2, 3],
            ProtectedEncryptionKey = [4, 5, 6]
        };
        await using (var previous = new MeshlineDbContext(database.Options, SqliteOpenMode.ReadWriteCreate))
        {
            await previous.GetService<IMigrator>().MigrateAsync("20261022102400_ResumableAccountEstablishment", token);
            await previous.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AccountEstablishment (Id, RelayId, OperationId, Step, CertificateValiditySeconds, RouteValiditySeconds, IsInitialState, DeviceAuthorizationConfirmed) VALUES ({1}, {"relay"}, {operationId}, {0}, {86400}, {172800}, {true}, {false})", token);
            previous.DevicePreparations.Add(draft);
            previous.SignedRequests.Add(new SignedRequestRecord
            {
                Method = "device.state.publish",
                RelayId = "relay",
                DocumentJson = "exact-signed-publication",
                Revision = 7,
                Pending = true,
                OperationKey = operationId.ToString("N")
            });
            await previous.SaveChangesAsync(token);
        }

        await MeshlineDatabase.MigrateAsync(database.Options, token);

        await using var upgraded = database.Open();
        var setup = await upgraded.AccountEstablishments.SingleAsync(token);
        Assert.Equal(operationId, setup.OperationId);
        Assert.Equal(86400, setup.CertificateValiditySeconds);
        Assert.Equal(172800, setup.RouteValiditySeconds);
        Assert.Null(setup.InitialPushGateway);
        var retained = await upgraded.DevicePreparations.SingleAsync(token);
        Assert.Equal(draft.OperationId, retained.OperationId);
        Assert.Equal(draft.DraftCertificateJson, retained.DraftCertificateJson);
        Assert.Equal(draft.ProtectedSigningKey, retained.ProtectedSigningKey);
        Assert.Equal(draft.ProtectedEncryptionKey, retained.ProtectedEncryptionKey);
        var publication = await upgraded.SignedRequests.SingleAsync(token);
        Assert.Equal("exact-signed-publication", publication.DocumentJson);
        Assert.Equal(operationId.ToString("N"), publication.OperationKey);
        Assert.True(publication.Pending);
        setup.InitialPushGateway = "https://push.example.com/callback";
        await upgraded.SaveChangesAsync(token);
        await using var reopened = database.Open();
        Assert.Equal(setup.InitialPushGateway, (await reopened.AccountEstablishments.SingleAsync(token)).InitialPushGateway);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Database_cannot_be_reused_by_another_account_or_network(bool otherAccount)
    {
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var signer = new AccountSigner();
        using var other = new AccountSigner();
        await using var pool = relay.Pool(signer);
        await using var first = new AccountManager(relay.Options(signer), database.Options, pool, signer);
        await first.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var options = otherAccount ? relay.Options(other) : new ClientOptions
        {
            AccountId = signer.AccountId,
            Context = TestNetwork.Context with
            {
                Reference = 1
            }
        };
        await using var second = new AccountManager(options, database.Options, pool);

        await Assert.ThrowsAsync<InvalidOperationException>(() => second.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Transaction_rolls_back_business_change_and_cursor_together()
    {
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using (var db = database.Open())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken: TestContext.Current.CancellationToken);
            db.Contacts.Add(Contact("a"));
            db.AccountTimelines.Add(new AccountTimelineRecord
            {
                RelayId = "relay",
                Sequence = 5
            });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var verify = database.Open();

        Assert.Empty(await verify.Contacts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await verify.AccountTimelines.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Message_uniqueness_is_sender_plus_id()
    {
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var instant = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(8));
        await using (var db = database.Open())
        {
            db.Messages.Add(Message("a", instant));
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var db = database.Open())
        {
            db.Messages.Add(Message("a", instant));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        await using (var db = database.Open())
        {
            db.Messages.Add(Message("b", instant));
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var verify = database.Open();
        var messages = await verify.Messages.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Message_dates_preserve_utc_instant()
    {
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        var instant = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(8));

        await using (var db = database.Open())
        {
            db.Messages.AddRange(Message("a", instant), Message("b", instant));
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var verify = database.Open();
        var messages = await verify.Messages.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, messages.Count);
        Assert.All(messages, message =>
        {
            Assert.Equal(instant, message.CreatedAt);
            Assert.Equal(TimeSpan.Zero, message.CreatedAt.Offset);
        });
    }

    static ContactStateRecord Contact(string id) => new()
    {
        AccountId = id,
        State = ContactRelationshipState.Active,
        UpdatedAt = DateTimeOffset.UnixEpoch
    };
    static StoredMessageRecord Message(string sender, DateTimeOffset time) => new()
    {
        Sender = sender,
        Recipient = "r",
        SenderDeviceId = "dev",
        MessageId = "msg",
        CreatedAt = time,
        PayloadType = "type"
    };
}
