using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Meshline.Tests.Components.Accounts;

public sealed class LocalDataIntegrityTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, SecretAccessFailure.Unknown)]
    [InlineData(false, SecretAccessFailure.TemporarilyUnavailable)]
    [InlineData(false, SecretAccessFailure.PermanentlyLost)]
    [InlineData(true, SecretAccessFailure.Unknown)]
    [InlineData(true, SecretAccessFailure.TemporarilyUnavailable)]
    [InlineData(true, SecretAccessFailure.PermanentlyLost)]
    public async Task Unreadable_draft_preserves_the_entire_operation_and_never_generates_replacement_keys(bool renewal, SecretAccessFailure failure)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        if (renewal)
        {
            await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
            fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        }
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync(renewal
                ? "CREATE TRIGGER fail_activation BEFORE UPDATE ON LocalDevice BEGIN SELECT RAISE(ABORT, 'interrupted'); END;"
                : "CREATE TRIGGER fail_activation BEFORE INSERT ON LocalDevice BEGIN SELECT RAISE(ABORT, 'interrupted'); END;", Token);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        await using (var db = fixture.Database.Open())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_activation;", Token);
        var before = await SnapshotAsync();
        await fixture.ReopenAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reads = 0;
            fixture.Protector.ReadFailure = purpose => purpose.Contains("device-encryption/", StringComparison.Ordinal)
                && ++reads >= (renewal ? 2 : 1) ? new SecretAccessException(failure, "draft unavailable") : null;
            fixture.Relay.Requests.Clear();

            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));

            Assert.Equal(AccountEstablishmentReason.LocalKeyAccessRequired, error.Reason);
            Assert.Equal(failure, error.KeyIssue);
            Assert.Equal(before, await SnapshotAsync());
            Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method.EndsWith(".publish", StringComparison.Ordinal));
        }

        // Temporary/unknown access can recover. Permanent loss remains unsupported;
        // the same diagnostic never authorizes replacing the retained local data.
        if (failure != SecretAccessFailure.PermanentlyLost)
        {
            fixture.Protector.ReadFailure = null;
            await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
            await using var db = fixture.Database.Open();
            Assert.Empty(await db.AccountEstablishments.ToListAsync(Token));
        }

        async Task<string> SnapshotAsync()
        {
            await using var db = fixture.Database.Open();
            return JsonSerializer.Serialize(new
            {
                Binding = await db.Bindings.SingleAsync(Token),
                Local = await db.LocalDevices.SingleOrDefaultAsync(Token),
                Draft = await db.DevicePreparations.SingleAsync(Token),
                Setup = await db.AccountEstablishments.SingleAsync(Token),
                Requests = await db.SignedRequests.OrderBy(value => value.Method).ToArrayAsync(Token)
            });
        }
    }
}
