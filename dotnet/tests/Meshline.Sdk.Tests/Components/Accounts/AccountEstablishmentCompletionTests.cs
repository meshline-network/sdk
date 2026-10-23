using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountEstablishmentCompletionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Expiry_during_profile_publication_retains_progress_and_renews_only_the_expired_authorization(bool routeExpires, bool reopen)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        fixture.Relay.Handler = (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            // Acceptance happens while authorized, but the response arrives later.
            if (request.Method == "profile.publish") fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        };
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
        {
            CertificateValidity = TimeSpan.FromSeconds(routeExpires ? 3600 : 1),
            RouteValidity = TimeSpan.FromSeconds(routeExpires ? 1 : 3600),
            InitialProfile = new() { Nickname = "retained", Bio = "history", PublicDiscovery = true }
        }, Token));
        Assert.Equal(AccountEstablishmentReason.AuthorizationNotYetEffective, error.Reason);
        var device = fixture.Client.Device!;
        var route = fixture.Client.Route!;
        var profile = fixture.Client.Profile!.ToJson();
        var input = "retained key"u8.ToArray();
        var signature = await fixture.Client.DeviceManager.SignAsync(input, Token);
        await using (var db = fixture.Database.Open())
        {
            Assert.Single(await db.AccountEstablishments.ToListAsync(Token));
            Assert.False((await db.SignedRequests.SingleAsync(value => value.Method == "profile.publish", Token)).Pending);
        }
        fixture.Relay.Handler = null;
        if (reopen) await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);

        Assert.Equal(device.GetDeviceId(TestNetwork.Context), fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(signature, await fixture.Client.DeviceManager.SignAsync(input, Token));
        Assert.Equal(profile, fixture.Client.Profile!.ToJson());
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        Assert.True(fixture.Client.Route!.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds());
        Assert.Null(fixture.Client.DeviceState.ValidateDeviceAuthorization(device.GetDeviceId(TestNetwork.Context), TestNetwork.Context));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "profile.publish");
        Assert.Equal(routeExpires ? 1 : 0, fixture.Relay.Requests.Count(request => request.Method == "account.route.publish"));
        Assert.Equal(routeExpires ? 0 : 1, fixture.Relay.Requests.Count(request => request.Method == "device.state.publish"));
        if (routeExpires) Assert.Equal(device.ToJson(), fixture.Client.Device.ToJson());
        else Assert.Equal(route.ToJson(), fixture.Client.Route.ToJson());
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Fact]
    public async Task Already_authorized_shortcut_rechecks_route_expiry_after_device_discovery()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Handler = (request, _) =>
        {
            var response = fixture.Relay.Respond(request);
            if (request.Method == "device.state.resolve") fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        };
        fixture.Relay.Requests.Clear();
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(cancellationToken: Token));
        Assert.Equal(AccountEstablishmentReason.AuthorizationNotYetEffective, error.Reason);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "device.state.publish" or "account.route.publish" or "profile.publish");
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Expiry_during_cleanup_retains_the_operation_and_renews_only_the_expired_part(bool routeExpires, bool afterCommit)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        var deleting = false;
        var expired = false;
        AccountEstablishmentRecord? original = null;
        using (var commands = new DatabaseObserver((name, data) =>
        {
            if (data is CommandEventData command && command.Command.Connection?.DataSource == fixture.Database.Options.Path
                && name.EndsWith("CommandExecuting", StringComparison.Ordinal)
                && command.Command.CommandText.Contains("DELETE FROM \"AccountEstablishment\"", StringComparison.Ordinal))
            {
                using var db = fixture.Database.Open();
                original = db.AccountEstablishments.AsNoTracking().Single();
                deleting = true;
                if (!afterCommit) { expired = true; fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2)); }
            }
            if (afterCommit && deleting && !expired && name.EndsWith("TransactionCommitted", StringComparison.Ordinal)
                && data is TransactionEndEventData transaction && transaction.Context?.Database.GetDbConnection().DataSource == fixture.Database.Options.Path)
            {
                expired = true;
                fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
            }
        }))
        {
            var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => fixture.Client.EstablishAccountAsync(new()
            {
                RouteValidity = TimeSpan.FromSeconds(routeExpires ? 1 : 3600),
                CertificateValidity = TimeSpan.FromSeconds(routeExpires ? 3600 : 1),
                InitialProfile = new() { Nickname = "retained", Bio = "history", PublicDiscovery = true }
            }, Token));
            Assert.Equal(AccountEstablishmentReason.AuthorizationNotYetEffective, error.Reason);
        }
        Assert.True(expired);
        Assert.NotNull(original);
        await using (var db = fixture.Database.Open())
        {
            var retained = await db.AccountEstablishments.SingleAsync(Token);
            Assert.Equal(original.OperationId, retained.OperationId);
            Assert.Equal(original.InitialProfileJson, retained.InitialProfileJson);
            Assert.Equal(original.RouteValiditySeconds, retained.RouteValiditySeconds);
            Assert.Equal(original.CertificateValiditySeconds, retained.CertificateValiditySeconds);
            Assert.Equal(AccountEstablishmentStep.InitializingProfile, retained.Step);
        }
        var device = fixture.Client.Device!;
        var route = fixture.Client.Route!;
        var profile = fixture.Client.Profile!.ToJson();
        var input = "retained key"u8.ToArray();
        var signature = await fixture.Client.DeviceManager.SignAsync(input, Token);
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device.GetDeviceId(TestNetwork.Context), fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(signature, await fixture.Client.DeviceManager.SignAsync(input, Token));
        Assert.Equal(profile, fixture.Client.Profile!.ToJson());
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        Assert.True(fixture.Client.Route!.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds());
        Assert.Null(fixture.Client.DeviceState.ValidateDeviceAuthorization(device.GetDeviceId(TestNetwork.Context), TestNetwork.Context));
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "profile.publish");
        Assert.Equal(routeExpires ? 1 : 0, fixture.Relay.Requests.Count(request => request.Method == "account.route.publish"));
        Assert.Equal(routeExpires ? 0 : 1, fixture.Relay.Requests.Count(request => request.Method == "device.state.publish"));
        if (routeExpires) Assert.Equal(device.ToJson(), fixture.Client.Device.ToJson());
        else Assert.Equal(route.ToJson(), fixture.Client.Route.ToJson());
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    [Fact]
    public async Task Cancellation_during_cleanup_rolls_back_and_retry_keeps_accepted_authorization()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using (var commands = new DatabaseObserver((name, data) =>
        {
            if (data is CommandEventData command && command.Command.Connection?.DataSource == fixture.Database.Options.Path
                && name.EndsWith("CommandExecuting", StringComparison.Ordinal)
                && command.Command.CommandText.Contains("DELETE FROM \"AccountEstablishment\"", StringComparison.Ordinal))
                cancellation.Cancel();
        }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.EstablishAccountAsync(new()
            {
                InitialProfile = new() { Nickname = "retained", Bio = "history", PublicDiscovery = true }
            }, cancellation.Token));
        }
        await using (var db = fixture.Database.Open())
            Assert.Single(await db.AccountEstablishments.ToListAsync(Token));
        var device = fixture.Client.Device!.ToJson();
        var profile = fixture.Client.Profile!.ToJson();
        await fixture.ReopenAsync();
        fixture.Relay.Requests.Clear();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device, fixture.Client.Device!.ToJson());
        Assert.Equal(profile, fixture.Client.Profile!.ToJson());
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method is "device.state.publish" or "account.route.publish" or "profile.publish");
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
    }

    sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    sealed class DatabaseObserver : IDisposable
    {
        readonly ConcurrentBag<IDisposable> subscriptions = [];
        readonly IDisposable all;
        public DatabaseObserver(Action<string, object?> callback)
        {
            all = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name == "Microsoft.EntityFrameworkCore")
                    subscriptions.Add(listener.Subscribe(new Observer<KeyValuePair<string, object?>>(pair =>
                    {
                        callback(pair.Key, pair.Value);
                    })));
            }));
        }
        public void Dispose()
        {
            all.Dispose();
            foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }
}
