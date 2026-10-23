using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text;

namespace Meshline.Tests.Components.Accounts;

public sealed class AccountPublicationFailureTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("meshline.account.route", "account.route.publish")]
    [InlineData("meshline.account.device.state", "device.state.publish")]
    public async Task Signing_timeout_before_a_publication_is_not_an_unknown_submission(string domain, string method)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.DisposeAsync();
        var signer = new TimeoutSigner(fixture.Account, domain);
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => client.EstablishAccountAsync(cancellationToken: Token));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == method);
        await using var db = fixture.Database.Open();
        Assert.False(await db.SignedRequests.AnyAsync(record => record.Method == method, Token));
        Assert.Equal(AccountEstablishmentReason.AccountStateUnavailable, error.Reason);
        var operation = await db.AccountEstablishments.SingleAsync(Token);
        var device = client.Device!;
        var input = "same prepared key"u8.ToArray();
        var signature = await client.DeviceManager.SignAsync(input, Token);
        await client.DisposeAsync();
        await fixture.ReopenAsync();
        await fixture.Client.EstablishAccountAsync(cancellationToken: Token);
        Assert.Equal(device.ToJson(), fixture.Client.Device!.ToJson());
        Assert.Equal(signature, await fixture.Client.DeviceManager.SignAsync(input, Token));
        Assert.Single(fixture.Client.DeviceState!.Certificates);
        await using var verify = fixture.Database.Open();
        Assert.Empty(await verify.AccountEstablishments.ToListAsync(Token));
        Assert.Equal(operation.OperationId, (await verify.DevicePreparations.SingleAsync(Token)).ActivatedOperationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Standalone_publication_preserves_signing_and_transport_exceptions(bool route)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await fixture.Client.DisposeAsync();
        var signer = new TimeoutSigner(fixture.Account, route ? "meshline.account.route" : "meshline.account.device.state");
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        Task Publish() => route ? client.AccountManager.PublishRouteAsync(fixture.Relay.RelayId, TimeSpan.FromDays(1), cancellationToken: Token)
            : client.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, cancellationToken: Token);
        await Assert.ThrowsAsync<TimeoutException>(Publish);
        signer.Fail = false;
        fixture.Relay.Handler = (request, _) => request.Method == (route ? "account.route.publish" : "device.state.publish")
            ? throw new HttpRequestException("response lost") : Task.FromResult(fixture.Relay.Respond(request));
        await Assert.ThrowsAsync<HttpRequestException>(Publish);
    }

    [Fact]
    public async Task Ensure_route_reports_signing_timeout_as_unavailable()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { RouteValidity = TimeSpan.FromSeconds(1) }, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Client.DisposeAsync();
        var signer = new TimeoutSigner(fixture.Account, "meshline.account.route");
        await using var client = new MeshlineClient(fixture.Relay.Options(fixture.Account), fixture.Database.Options,
            fixture.Pool, fixture.Protector, signer);
        await client.InitializeAsync(Token);
        var error = await Assert.ThrowsAsync<AccountRouteException>(() => client.AccountManager.EnsureRouteAsync(
            fixture.Relay.RelayId, TimeSpan.FromDays(1), Token));
        Assert.Equal(AccountRouteFailure.StateUnavailable, error.Failure);
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    sealed class TimeoutSigner(AccountSigner inner, string domain) : IAccountSigner
    {
        public bool Fail { get; set; } = true;
        public string AccountId => inner.AccountId;
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) =>
            Fail && Encoding.UTF8.GetString(input.Span).Contains(domain, StringComparison.Ordinal)
                ? throw new TimeoutException("wallet timeout before signature") : inner.SignAsync(input, cancellationToken);
    }
}
