using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Storage;

public sealed class QueryReaderTests
{
    [Fact]
    public async Task Query_reader_holds_snapshot_across_writes_and_new_reader_refreshes()
    {
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using (var db = database.Open())
        {
            db.Contacts.AddRange(Contact("a"), Contact("b"), Contact("c"));
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var reader = await QueryReader<string>.OpenAsync(database.Options, db => db.Contacts.OrderBy(c => c.AccountId).Select(c => c.AccountId), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["a"], await reader.ReadNextAsync(1, cancellationToken: TestContext.Current.CancellationToken));

        await using (var db = database.Open())
        {
            await db.Contacts.Where(c => c.AccountId == "b").ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            db.Contacts.Add(Contact("aa"));
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(["b", "c"], await reader.ReadNextAsync(10, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await reader.ReadNextAsync(10, cancellationToken: TestContext.Current.CancellationToken));

        await using var refreshed = await QueryReader<string>.OpenAsync(database.Options, db => db.Contacts.OrderBy(c => c.AccountId).Select(c => c.AccountId), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["a", "aa", "c"], await refreshed.ReadNextAsync(10, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reader_rejects_invalid_counts_cancellation_and_reads_after_disposal()
    {
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using var reader = await QueryReader<string>.OpenAsync(database.Options, db => db.Contacts.OrderBy(c => c.AccountId).Select(c => c.AccountId), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadNextAsync(0, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadNextAsync(1, new CancellationToken(true)));

        await reader.DisposeAsync();
        await reader.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadNextAsync(1, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_reads_do_not_repeat_or_omit_records()
    {
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using (var db = database.Open())
        {
            db.Contacts.AddRange(Enumerable.Range(0, 20).Select(i => Contact(i.ToString("D2"))));
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var reader = await QueryReader<string>.OpenAsync(database.Options, db => db.Contacts.OrderBy(c => c.AccountId).Select(c => c.AccountId), cancellationToken: TestContext.Current.CancellationToken);
        var pages = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => reader.ReadNextAsync(5)));

        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString("D2")), pages.SelectMany(p => p).Order());
    }

    static ContactStateRecord Contact(string id) => new()
    {
        AccountId = id,
        State = ContactRelationshipState.Active,
        UpdatedAt = DateTimeOffset.UnixEpoch
    };
}
