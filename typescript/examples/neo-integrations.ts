import { RpcRelayRegistry, Nep6AccountSigner, type NetworkContext, type RelayFetch, type RandomSource } from '@meshline/sdk';

/** The caller supplies trusted configuration and may inject Node/Expo transports and randomness. */
export async function openNeoIntegrations(context: NetworkContext, rpcUrl: string, walletJson: string, password: string,
    options: { accountIndex?: number; fetch?: RelayFetch; random?: RandomSource } = {}) {
    const registry = new RpcRelayRegistry({ context, rpcUrl, ...(options.fetch === undefined ? {} : { fetch: options.fetch }) });
    const signer = await Nep6AccountSigner.fromJson(walletJson, password, { context,
        ...(options.accountIndex === undefined ? {} : { accountIndex: options.accountIndex }),
        ...(options.random === undefined ? {} : { random: options.random }) });
    return { registry, signer, dispose: () => signer.dispose() };
}
