using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Accounts;

public sealed class DevicePublicationBoundaryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_and_migration_keep_preparation_and_publication_in_one_mutation(bool migrate)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync(false);
        await fixture.Client.EstablishAccountAsync(new() { CertificateValidity = TimeSpan.FromSeconds(10) }, Token);
        var deviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(20));
        using var target = new OfflineRelay("target.test");
        fixture.Relay.LinkTo(target);
        var destination = migrate ? target : fixture.Relay;
        destination.Requests.Clear();
        var entered = AsyncTest.Signal();
        var release = AsyncTest.Signal();
        using var competingCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<DeviceCertificate>? competingRenewal = null;
        string? prepared = null;
        fixture.Client.DeviceManager.DeviceChanged += (_, _) =>
        {
            if (prepared is not null) return;
            prepared = fixture.Client.Device!.ToJson();
            competingRenewal = fixture.Client.DeviceManager.RenewDeviceAsync(
                TimeSpan.FromDays(30), competingCancellation.Token);
        };
        destination.Handler = async (request, token) =>
        {
            if (request.Method == "device.state.publish")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return destination.Respond(request);
        };
        var publication = migrate ? fixture.Client.ChangeHomeRelayAsync(target.RelayId, Token)
            : fixture.Client.RecoverAccountAsync(new() { RelayId = fixture.Relay.RelayId, CertificateValidity = TimeSpan.FromDays(7) }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.NotNull(competingRenewal);
            Assert.False(competingRenewal.IsCompleted);
            competingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => competingRenewal);
            Assert.Equal(prepared, fixture.Client.Device!.ToJson());
        }
        finally
        {
            competingCancellation.Cancel();
            release.TrySetResult();
        }
        await publication.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal(deviceId, fixture.Client.Device!.GetDeviceId(TestNetwork.Context));
        Assert.Equal(prepared, Assert.Single(destination.Devices[fixture.Account.AccountId].Certificates).ToJson());
        Assert.Single(destination.Requests, request => request.Method == "device.state.publish");
        Assert.Equal(migrate ? 10 : (long)TimeSpan.FromDays(7).TotalSeconds,
            fixture.Client.Device.ExpiresAt - fixture.Client.Device.NotBefore);
        await using var verify = fixture.Database.Open();
        Assert.False((await verify.SignedRequests.SingleAsync(row => row.Method == "device.state.publish", Token)).Pending);
    }
}
