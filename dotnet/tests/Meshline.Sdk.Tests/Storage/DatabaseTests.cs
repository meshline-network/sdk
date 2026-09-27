using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

        Assert.Single(await db.Database.GetAppliedMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await db.Bindings.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
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
