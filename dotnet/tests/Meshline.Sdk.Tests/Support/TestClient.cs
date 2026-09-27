using Meshline.Models.Client;
using Meshline.Transport;

namespace Meshline.Tests.Support;

internal sealed class TestClient : IAsyncDisposable
{
    public OfflineRelay Relay { get; } = new();
    public AccountSigner Account { get; }
    public TestDatabase Database { get; } = new();
    public SecretProtector Protector { get; } = new();
    public RelayClientPool Pool { get; }
    public MeshlineClient Client { get; private set; }

    public TestClient(AccountSigner? account = null)
    {
        Account = account ?? new();
        Pool = Relay.Pool(Account);
        Client = Create();
    }

    MeshlineClient Create() => new(Relay.Options(Account), Database.Options, Pool, Protector, Account);
    public async Task InitializeAsync(bool establish = true)
    {
        await Database.MigrateAsync();
        await Client.InitializeAsync(TestContext.Current.CancellationToken);
        if (establish)
            await Client.EstablishAccountAsync(new AccountEstablishmentOptions { RelayId = Relay.RelayId }, TestContext.Current.CancellationToken);
    }

    public async Task ReopenAsync()
    {
        await Client.DisposeAsync();
        Client = Create();
        await Client.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
        }
        finally
        {
            try
            {
                await Pool.DisposeAsync();
            }
            finally
            {
                Relay.Dispose();
                Account.Dispose();
                await Database.DisposeAsync();
            }
        }
    }
}
