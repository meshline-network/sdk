import { expect, test } from 'vitest';
import { CID } from 'multiformats/cid';
import { base58btc } from 'multiformats/bases/base58';
import { create } from 'multiformats/hashes/digest';
import {
    NetworkContext, accountPublicKey, getRelayId, relayDescriptorCodec, relayDescriptorInput, relayInfoCodec,
    signAccount, validateRelayDescriptor, validateRelayEndpoint, validateRelayInfo, type RelayDescriptor,
} from '../../packages/sdk/src/index.js';

const context = NetworkContext.parse('neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70');
const key = new Uint8Array(32).fill(7);
const publicKey = accountPublicKey(key);
const digest = create(0x12, new Uint8Array(32).fill(1));
export const peerId = base58btc.encode(digest.bytes).slice(1);
const peerCid = CID.createV1(0x72, digest).toString();
const tcp = `/dns4/relay.example/tcp/4201/p2p/${peerId}`;
export function descriptor(overrides: Partial<RelayDescriptor> = {}): RelayDescriptor {
    const unsigned: RelayDescriptor = { relayId: getRelayId(publicKey), publicKey, endpoints: ['https://relay.example/meshline/v1', 'wss://relay.example/meshline/v1', tcp],
        expiresAt: 2000000000, capabilities: ['channel.host.v1', 'group.host.v1'], relaySignature: new Uint8Array(64), ...overrides };
    return { ...unsigned, relaySignature: signAccount(relayDescriptorInput(unsigned, context), key) };
}

test('signed descriptor preserves extensions and verifies Relay ID and network', () => {
    const value = descriptor({ additionalProperties: { future: { signed: true } } });
    validateRelayDescriptor(value, context, 1730000000);
    validateRelayDescriptor(relayDescriptorCodec.parse(relayDescriptorCodec.stringify(value)), context, 1730000000);
    expect(() => validateRelayDescriptor({ ...value, additionalProperties: { future: false } }, context, 1730000000)).toThrow('signature');
    expect(() => validateRelayDescriptor(value, new NetworkContext(860833103, context.registry), 1730000000)).toThrow();
    expect(() => validateRelayDescriptor(descriptor({ relayId: '0x' + '0'.repeat(40) }), context, 1730000000)).toThrow('signature');
    expect(() => validateRelayDescriptor(value, context, value.expiresAt)).toThrow('expired');
});

test('HTTPS plus libp2p is mandatory and duplicate endpoints or peers fail', () => {
    for (const endpoints of [[], ['https://relay.example'], [tcp], ['https://relay.example', tcp, tcp],
        ['https://relay.example', tcp, `/dns4/other.example/tcp/4201/p2p/${base58btc.encode(create(0x12, new Uint8Array(32).fill(2)).bytes).slice(1)}`]]) {
        expect(() => validateRelayDescriptor(descriptor({ endpoints }), context, 1730000000)).toThrow();
    }
});

test('alternate encodings of the same peer are accepted; future transport strings remain signed', () => {
    const value = descriptor({ endpoints: ['https://relay.example', tcp, `/ip6/2001:db8::1/tcp/4201/p2p/${peerCid}`, 'future://opaque-endpoint'] });
    validateRelayDescriptor(value, context, 1730000000);
    expect(validateRelayEndpoint(`/ip4/127.0.0.1/tcp/4201/ws/p2p/${peerCid}`)).toEqual({ kind: 'libp2p-tcp', peerId });
    expect(validateRelayEndpoint('future://opaque-endpoint')).toEqual({ kind: 'unknown' });
});

test.each([
    `/dns4/relay.example/tcp/0/p2p/${peerId}`, `/dns4/relay.example/tcp/65536/p2p/${peerId}`,
    `/dns4/relay.example/tcp/-1/p2p/${peerId}`, `/dns4/relay.example/tcp/1.0/p2p/${peerId}`,
    `/dns4/relay.example/tcp/4/p2p/${peerId}/`, `/dns4/relay.example/tcp/4`,
    `/dns4/relay.example/udp/3/tcp/4/p2p/${peerId}`, `/dns4/relay.example/tcp/4/tcp/5/p2p/${peerId}`,
    `/dns4/relay.example/tcp/4/p2p/${peerId}/p2p/${peerId}`, `/ip4/999.1.1.1/tcp/4/p2p/${peerId}`,
    `/ip6/invalid/tcp/4/p2p/${peerId}`, `/dns4/127.0.0.1/tcp/4/p2p/${peerId}`,
    `/dns4/relay.example/tcp/4/p2p/not-a-peer`, ` /dns4/relay.example/tcp/4/p2p/${peerId}`,
    `/dns4/relay.example/tcp/4/p2p/${CID.createV1(0x55, digest)}`,
])('rejects malformed libp2p endpoint %s', endpoint => { expect(() => validateRelayEndpoint(endpoint)).toThrow(); });

test('identity peer digests are restricted to 1..42 bytes', () => {
    for (const length of [1, 42]) expect(validateRelayEndpoint(`/dns4/relay.example/tcp/1/p2p/${base58btc.encode(create(0, new Uint8Array(length).fill(2)).bytes).slice(1)}`).kind).toBe('libp2p-tcp');
    for (const length of [0, 43]) expect(() => validateRelayEndpoint(`/dns4/relay.example/tcp/1/p2p/${base58btc.encode(create(0, new Uint8Array(length).fill(2)).bytes).slice(1)}`)).toThrow();
});

test.each([['same', 'same'], [''], ['😀'.repeat(33)], Array.from({ length: 65 }, (_, index) => String(index))])('capability bounds %j', (...capabilities) => {
    expect(() => validateRelayDescriptor(descriptor({ capabilities }), context, 1730000000)).toThrow();
});

test('relay info requires capability-dependent limits and distinguishes zero subscriptions from missing', () => {
    const wire = { relay_id: getRelayId(publicKey), name: 'Meshline 测试', server_time: 1730000000, limits: { message_retention: 1 } };
    validateRelayInfo(relayInfoCodec.decode(wire));
    expect(() => validateRelayInfo(relayInfoCodec.decode(wire), descriptor())).toThrow('limits');
    const complete = { ...wire, limits: { ...wire.limits, channel_timeline_retention: 1, max_channel_subscriptions: 0,
        group_message_retention: 1, max_group_members: 1, max_group_subscriptions: 0, max_group_invite_ttl: 1 } };
    validateRelayInfo(relayInfoCodec.decode(complete), descriptor());
    expect(() => relayInfoCodec.decode({ ...complete, limits: { ...complete.limits, max_group_subscriptions: null } })).toThrow();
    expect(() => validateRelayInfo(relayInfoCodec.decode({ ...complete, limits: { ...complete.limits, group_message_retention: 0 } }))).toThrow();
});
