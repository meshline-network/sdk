import * as sdk from '@meshline/sdk';

export const context = sdk.NetworkContext.parse('neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70');
export const relayPrivateKey = new Uint8Array(32).fill(7); // Public fixture key.
export function signedDescriptor(random: sdk.RandomSource): sdk.RelayDescriptor {
    const publicKey = sdk.accountPublicKey(relayPrivateKey);
    const value: sdk.RelayDescriptor = { relayId: sdk.getRelayId(publicKey), publicKey,
        endpoints: ['https://relay.example/v1', '/dns4/relay.example/tcp/4201/p2p/QmNQa1FSTXNHmrjjfgUW3Px3Vkke4oKiFWdigWkYSux2Pi'], capabilities: ['channel.host.v1', 'group.host.v1'],
        expiresAt: 2000000000, relaySignature: new Uint8Array(64) };
    return { ...value, relaySignature: sdk.signAccount(sdk.relayDescriptorInput(value, context), relayPrivateKey, random) };
}
export class TestRegistry implements sdk.RelayRegistry {
    readonly context = context;
    readonly entry: sdk.RelayEntry;
    constructor(random: sdk.RandomSource) {
        this.entry = { relayId: signedDescriptor(random).relayId, endpoint: 'https://bootstrap.example', status: 'active', updatedAt: 1730000000000n };
    }
    async getRelay(): Promise<sdk.RelayEntry> { return this.entry; }
    async *getRelays(): AsyncIterable<sdk.RelayEntry> { yield this.entry; }
}
