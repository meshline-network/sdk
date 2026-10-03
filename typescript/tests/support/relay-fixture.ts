import { base58btc } from 'multiformats/bases/base58';
import { create } from 'multiformats/hashes/digest';
import { NetworkContext, accountPublicKey, getRelayId, relayDescriptorInput, signAccount, type RelayDescriptor, type RelayEntry, type RelayRegistry } from '../../packages/sdk/src/index.js';

export const context = NetworkContext.parse('neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70');
export const relayPrivateKey = new Uint8Array(32).fill(7);
export const peerId = base58btc.encode(create(0x12, new Uint8Array(32).fill(1)).bytes).slice(1);
export function signedDescriptor(overrides: Partial<RelayDescriptor> = {}): RelayDescriptor {
    const publicKey = accountPublicKey(relayPrivateKey);
    const value: RelayDescriptor = { relayId: getRelayId(publicKey), publicKey,
        endpoints: ['https://relay.example/v1', 'wss://relay.example/v1', `/dns4/relay.example/tcp/4201/p2p/${peerId}`],
        capabilities: ['channel.host.v1', 'group.host.v1'], expiresAt: 2000000000, relaySignature: new Uint8Array(64), ...overrides };
    return { ...value, relaySignature: signAccount(relayDescriptorInput(value, context), relayPrivateKey) };
}

export class TestRegistry implements RelayRegistry {
    context = context;
    reads = 0;
    entry: RelayEntry | undefined = { relayId: signedDescriptor().relayId, endpoint: 'https://bootstrap.example', status: 'active', updatedAt: 1730000000000n };
    async getRelay(): Promise<RelayEntry | undefined> { this.reads++; return this.entry; }
    async *getRelays(): AsyncIterable<RelayEntry> { if (this.entry) yield this.entry; }
}
