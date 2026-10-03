using Meshline.Interactions;
using Meshline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Meshline.Examples;

public static class NeoIntegrations
{
    #region neo-registry
    public static void RegisterRpcRegistry(
        IServiceCollection services, HttpClient rpcHttp, NetworkContext context, Uri rpcUrl)
    {
        // This is application registration code; the SDK never adds this service automatically.
        services.AddSingleton<IRelayRegistry>(_ => new RpcRelayRegistry(rpcHttp,
            new RpcRelayRegistryOptions { Context = context, RpcUrl = rpcUrl }));
    }
    #endregion

    #region nep6-signer
    public static Nep6AccountSigner OpenAccount(
        string walletPath, string walletPassword, NetworkContext context, int accountIndex = 0)
    {
        // The application chooses the path/password and disposes the returned signer.
        return Nep6AccountSigner.Load(walletPath, walletPassword, context, accountIndex);
    }
    #endregion
}
