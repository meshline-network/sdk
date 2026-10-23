using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountProfileInitializationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_query_failure_is_unavailable_and_retry_preserves_authorization(bool existingAccount)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(existingAccount);
        if (existingAccount)
        {
            await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "retained", Bio = "history", PublicDiscovery = true }, Token);
            await using var db = fixture.Database.Open();
            db.AccountEstablishments.Add(new() { OperationId = Guid.NewGuid(), RelayId = fixture.Relay.RelayId });
            await db.SaveChangesAsync(Token);
        }
        var profile = fixture.Client.Profile?.ToJson();
        fixture.Relay.Requests.Clear();
        var expected = new HttpRequestException("Profile query unavailable");
        fixture.Relay.Handler = (request, _) => request.Method == "profile.resolve"
            ? throw expected : Task.FromResult(fixture.Relay.Respond(request));
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        Assert.Same(expected, error.InnerException);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "profile.publish");
        var device = fixture.Client.Device!.ToJson();
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        if (existingAccount) Assert.Equal(profile, fixture.Client.Profile!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "account.route.publish" or "device.state.publish");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_profile_submission_is_reconciled_or_replayed_exactly_after_reopen(bool accepted)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var expected = new HttpRequestException("Profile response lost");
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "profile.publish")
            {
                if (accepted) fixture.Relay.Respond(request).Dispose();
                throw expected;
            }
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            InitialProfile = new() { Nickname = "retained", Bio = "history", PublicDiscovery = true }
        }, Token));
        Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
        Assert.Same(expected, error.InnerException);
        var pending = Assert.Single(fixture.Relay.Requests, request => request.Method == "profile.publish").Body;
        var device = fixture.Client.Device!.ToJson();
        await using (var db = fixture.Database.Open())
        {
            Assert.Single(await db.AccountEstablishments.ToListAsync(Token));
            Assert.True((await db.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);
        }
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.Equal("retained", fixture.Client.Profile!.Nickname);
        Assert.Equal("history", fixture.Client.Profile.Bio);
        Assert.True(fixture.Client.Profile.PublicDiscovery);
        Assert.Equal(accepted ? 0 : 1, fixture.Relay.Requests.Count(request => request.Method == "profile.publish"));
        Assert.All(fixture.Relay.Requests.Where(request => request.Method == "profile.publish"), request => Assert.Equal(pending, request.Body));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "account.route.publish" or "device.state.publish");
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.False((await verify.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);
    }

    [Theory]
    [InlineData("bad_request", false)]
    [InlineData("internal_error", true)]
    public async Task Profile_relay_errors_distinguish_definitive_rejection_from_unknown_outcome(string code, bool unknown)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "profile.publish"
            ? OfflineRelay.Error(code) : fixture.Relay.Respond(request));
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(unknown ? AccountEstablishmentReason.SubmissionOutcomeUnknown : AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        Assert.IsType<RelayException>(error.InnerException);
        await using var verify = fixture.Database.Open();
        Assert.Single(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Equal(unknown, (await verify.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_profile_submission_preserves_caller_cancellation_or_reports_unknown_owner_outcome(bool disposeManager)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.publish")
            {
                entered.TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var setup = fixture.Client.EstablishAccountAsync(cancellationToken: cancellation.Token);
        Task disposal = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (disposeManager) disposal = fixture.Client.ProfileManager.DisposeAsync().AsTask();
            else cancellation.Cancel();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(setup.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (disposeManager)
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => setup);
            Assert.Equal(AccountEstablishmentReason.SubmissionOutcomeUnknown, error.Reason);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        }
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup);
        var pending = Assert.Single(fixture.Relay.Requests, request => request.Method == "profile.publish").Body;
        var device = fixture.Client.Device!.ToJson();
        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.Equal(pending, Assert.Single(fixture.Relay.Requests, request => request.Method == "profile.publish").Body);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "device.state.publish" or "account.route.publish");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_account_setup_must_not_overwrite_a_newer_local_profile_publication(bool sameSecond)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "before", Bio = "before", PublicDiscovery = false }, Token);
        await using (var db = fixture.Database.Open())
        {
            db.AccountEstablishments.Add(new()
            {
                OperationId = Guid.NewGuid(),
                RelayId = fixture.Relay.RelayId,
                Step = AccountEstablishmentStep.InitializingProfile,
                DeviceAuthorizationConfirmed = true
            });
            await db.SaveChangesAsync(Token);
        }
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        var intercept = 1;
        fixture.Relay.Handler = async (request, token) =>
        {
            var response = fixture.Relay.Respond(request);
            if (request.Method == "profile.resolve" && Interlocked.Exchange(ref intercept, 0) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return response;
        };
        var setup = fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (!sameSecond) fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "after", Bio = "after", PublicDiscovery = true }, Token);
            Assert.Equal("after", fixture.Client.Profile!.Nickname);
        }
        finally { release.TrySetResult(); }
        await setup.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var cached = fixture.Client.Profile!;
        Assert.Equal("after", cached.Nickname);
        Assert.Equal("after", cached.Bio);
        Assert.True(cached.PublicDiscovery);
        Assert.Equal("after", fixture.Relay.Profiles[fixture.Account.AccountId].Profile.Nickname);
        await fixture.ReopenAsync();
        Assert.Equal(cached.ToJson(), fixture.Client.Profile!.ToJson());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Profile_initialization_is_drained_by_its_owner_and_resumes_after_reopen(bool initialAccount, bool disposeClient)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(!initialAccount);
        if (!initialAccount)
        {
            await fixture.Client.ProfileManager.UpdateProfileAsync(new() { Nickname = "retained", Bio = "history", PublicDiscovery = true }, Token);
            // Resume an existing account whose authorization finished before the
            // profile read. This must use the read-only initialization branch.
            await using var db = fixture.Database.Open();
            db.AccountEstablishments.Add(new()
            {
                OperationId = Guid.NewGuid(),
                RelayId = fixture.Relay.RelayId,
            });
            await db.SaveChangesAsync(Token);
        }
        var previousProfile = fixture.Client.Profile?.ToJson();
        var manager = fixture.Client.ProfileManager;
        var entered = AsyncTest.Signal();
        var canceled = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method == "profile.resolve")
            {
                entered.TrySetResult();
                await AsyncTest.WaitForCancellationAndCleanupAsync(token, canceled, release.Task);
            }
            return fixture.Relay.Respond(request);
        };
        var establishment = fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Task disposal;
        string device;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            device = fixture.Client.Device!.ToJson();
            disposal = disposeClient ? fixture.Client.DisposeAsync().AsTask() : manager.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(disposal.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (disposeClient)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => establishment);
        else
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => establishment);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        }
        await using (var db = fixture.Database.Open())
        {
            Assert.Single(await db.AccountEstablishments.ToListAsync(Token));
        }

        fixture.Relay.Handler = null;
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(device, fixture.Client.Device!.ToJson());
        if (!initialAccount) Assert.Equal(previousProfile, fixture.Client.Profile!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, value => value.Method is "device.state.publish" or "account.route.publish");
        Assert.Equal(initialAccount ? 1 : 0, fixture.Relay.Requests.Count(value => value.Method == "profile.publish"));
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }
}
