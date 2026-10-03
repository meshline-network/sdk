import { multiaddr } from '@multiformats/multiaddr';
import { bases } from 'multiformats/basics';
import { base58btc } from 'multiformats/bases/base58';
import { create as createDigest, decode as decodeDigest } from 'multiformats/hashes/digest';
import { requireLength } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { validateRelayId, verifyRelay } from '../identity/neo.js';
import { array, bytes, defineCodec, integer, text, type ExtensibleModel } from '../protocol/codec.js';
import { NetworkContext, signingInput } from '../protocol/context.js';
import { encodeUtf8, equalBytes } from '../protocol/encoding.js';
import { requireSafeInteger } from '../protocol/json.js';
import { webEndpoint } from '../transport/endpoint.js';

export interface RelayDescriptor extends ExtensibleModel {
    readonly relayId: string;
    readonly publicKey: Uint8Array;
    readonly endpoints: readonly string[];
    readonly capabilities?: readonly string[];
    readonly expiresAt: number;
    readonly relaySignature: Uint8Array;
}
export const relayDescriptorCodec = defineCodec<RelayDescriptor>({
    relayId: { wire: 'relay_id', codec: text }, publicKey: { wire: 'public_key', codec: bytes },
    endpoints: { wire: 'endpoints', codec: array(text) }, capabilities: { wire: 'capabilities', codec: array(text), optional: true },
    expiresAt: { wire: 'expires_at', codec: integer }, relaySignature: { wire: 'relay_signature', codec: bytes },
}, 'meshline.relay.descriptor');

export function relayDescriptorInput(descriptor: RelayDescriptor, context: NetworkContext): Uint8Array {
    return signingInput(relayDescriptorCodec.encode(descriptor), context, ['relay_signature']);
}

/** Normalized multihash identity, independent of a peer ID's allowed base/CID spelling. */
function peerIdentity(value: string): string {
    let hashBytes: Uint8Array;
    if (value.startsWith('Qm') || value.startsWith('1')) hashBytes = base58btc.decode('z' + value);
    else {
        const base = Object.values(bases).find(base => value.startsWith(base.prefix));
        if (!base) throw new ProtocolError('invalid_peer', 'Unknown peer ID multibase.');
        const cid = base.decode(value);
        if (cid[0] !== 1 || cid[1] !== 0x72) throw new ProtocolError('invalid_peer', 'Peer IDs require CIDv1 libp2p-key.');
        hashBytes = cid.subarray(2);
    }
    const digest = decodeDigest(hashBytes);
    if (!(digest.code === 0x12 && digest.size === 32 || digest.code === 0 && digest.size > 0 && digest.size <= 42)
        || !equalBytes(hashBytes, createDigest(digest.code, digest.digest).bytes))
        throw new ProtocolError('invalid_peer', 'Invalid or noncanonical peer multihash.');
    return base58btc.encode(hashBytes).slice(1);
}

export type RelayEndpoint = { readonly kind: 'https' | 'wss' | 'unknown' } | { readonly kind: 'libp2p-tcp'; readonly peerId: string };
export function validateRelayEndpoint(endpoint: string): RelayEndpoint {
    if (typeof endpoint !== 'string' || endpoint.length === 0) throw new ProtocolError('invalid_endpoint', 'Relay endpoints must be nonempty strings.');
    const candidate = endpoint.trimStart();
    if (/^(https|wss):/i.test(candidate)) { const url = webEndpoint(endpoint); return { kind: url.protocol === 'https:' ? 'https' : 'wss' }; }
    if (!candidate.startsWith('/') || !candidate.includes('/tcp/') && !candidate.endsWith('/tcp')) return { kind: 'unknown' };
    if (!endpoint.startsWith('/') || endpoint.endsWith('/') || endpoint.includes('//') || /[\s\p{Cc}]/u.test(endpoint))
        throw new ProtocolError('invalid_endpoint', 'Malformed libp2p TCP endpoint.');
    try {
        const parts = endpoint.split('/');
        if (parts.at(-2) !== 'p2p') throw new Error('Endpoint must end with a peer ID.');
        const peerId = peerIdentity(parts.at(-1)!);
        for (let i = 0; i < parts.length; i++) if (parts[i] === 'tcp' && !/^[0-9]+$/.test(parts[i + 1] ?? '')) throw new Error('Invalid TCP port.');
        const address = multiaddr(endpoint.slice(0, endpoint.lastIndexOf('/p2p/')) + '/p2p/' + peerId);
        // Parsing textual components alone is lazy; encoding validates IP and port values.
        void address.bytes;
        const components = address.getComponents();
        if (components.length < 3 || components[1]!.name !== 'tcp' || Number(components[1]!.value) <= 0
            || components.filter(item => item.name === 'tcp').length !== 1 || components.filter(item => item.name === 'p2p').length !== 1
            || components.at(-1)!.name !== 'p2p') throw new Error('Expected host/TCP/.../peer endpoint.');
        const host = components[0]!;
        if (!['ip4', 'ip6', 'dns', 'dns4', 'dns6'].includes(host.name)) throw new Error('Invalid multiaddr host type.');
        if (host.name.startsWith('dns')) {
            const hostname = parseAbsoluteUrl(`https://${host.value}`).hostname;
            if (hostname.startsWith('[') || /^[0-9.]+$/.test(hostname) || hostname.length > 255
                || !hostname.replace(/\.$/, '').split('.').every(label => /^[a-z0-9_][a-z0-9_-]{0,62}$/i.test(label))) throw new Error('Invalid DNS host.');
        }
        return { kind: 'libp2p-tcp', peerId };
    } catch (cause) { throw new ProtocolError('invalid_endpoint', 'Invalid libp2p TCP relay endpoint.', { cause }); }
}

export function validateRelayDescriptor(descriptor: RelayDescriptor, context: NetworkContext, now: number): void {
    validateCompleteObject(relayDescriptorCodec.encode(descriptor));
    requireSafeInteger(now, 0); requireSafeInteger(descriptor.expiresAt, 0);
    validateRelayId(descriptor.relayId);
    requireLength(descriptor.publicKey, 33, 'Relay public key'); requireLength(descriptor.relaySignature, 64, 'Relay signature');
    if (descriptor.expiresAt <= now) throw new ProtocolError('expired_descriptor', 'The relay descriptor has expired.');
    if (!Array.isArray(descriptor.endpoints) || descriptor.endpoints.length === 0) throw new ProtocolError('invalid_endpoint', 'Relay endpoints must be a nonempty array.');
    const endpoints = new Set<string>();
    let hasHttps = false;
    let peer: string | undefined;
    for (const endpoint of descriptor.endpoints) {
        if (endpoints.has(endpoint)) throw new ProtocolError('duplicate_endpoint', 'Relay endpoints cannot contain duplicates.');
        endpoints.add(endpoint);
        const validated = validateRelayEndpoint(endpoint);
        if (validated.kind === 'https') hasHttps = true;
        if (validated.kind === 'libp2p-tcp') {
            if (peer !== undefined && peer !== validated.peerId) throw new ProtocolError('invalid_peer', 'All libp2p TCP endpoints must identify the same peer.');
            peer = validated.peerId;
        }
    }
    if (!hasHttps || peer === undefined) throw new ProtocolError('missing_endpoint', 'A relay must advertise HTTPS and libp2p TCP endpoints.');
    const capabilities = descriptor.capabilities ?? [];
    if (!Array.isArray(capabilities) || capabilities.length > 64 || new Set(capabilities).size !== capabilities.length)
        throw new ProtocolError('invalid_capabilities', 'Capabilities must be distinct and contain at most 64 entries.');
    for (const value of capabilities) if (value.length === 0 || encodeUtf8(value).length > 128) throw new ProtocolError('invalid_capabilities', 'Capabilities must contain 1 to 128 UTF-8 bytes.');
    if (!verifyRelay(descriptor.relayId, descriptor.publicKey, relayDescriptorInput(descriptor, context), descriptor.relaySignature))
        throw new ProtocolError('invalid_signature', 'The descriptor signature does not identify this relay.');
}

export interface RelayLimits extends ExtensibleModel {
    readonly messageRetention: number;
    readonly channelTimelineRetention?: number;
    readonly maxChannelSubscriptions?: number;
    readonly groupMessageRetention?: number;
    readonly maxGroupMembers?: number;
    readonly maxGroupSubscriptions?: number;
    readonly maxGroupInviteTtl?: number;
}
export const relayLimitsCodec = defineCodec<RelayLimits>({
    messageRetention: { wire: 'message_retention', codec: integer },
    channelTimelineRetention: { wire: 'channel_timeline_retention', codec: integer, optional: true },
    maxChannelSubscriptions: { wire: 'max_channel_subscriptions', codec: integer, optional: true },
    groupMessageRetention: { wire: 'group_message_retention', codec: integer, optional: true },
    maxGroupMembers: { wire: 'max_group_members', codec: integer, optional: true },
    maxGroupSubscriptions: { wire: 'max_group_subscriptions', codec: integer, optional: true },
    maxGroupInviteTtl: { wire: 'max_group_invite_ttl', codec: integer, optional: true },
});
export interface RelayInfo extends ExtensibleModel { readonly relayId: string; readonly name: string; readonly serverTime: number; readonly limits: RelayLimits }
export const relayInfoCodec = defineCodec<RelayInfo>({
    relayId: { wire: 'relay_id', codec: text }, name: { wire: 'name', codec: text }, serverTime: { wire: 'server_time', codec: integer }, limits: { wire: 'limits', codec: relayLimitsCodec },
});
export function validateRelayInfo(info: RelayInfo, descriptor?: RelayDescriptor): void {
    validateCompleteObject(relayInfoCodec.encode(info));
    validateRelayId(info.relayId); requireSafeInteger(info.serverTime, 0);
    if (info.name.trim().length === 0 || encodeUtf8(info.name).length > 256) throw new ProtocolError('invalid_name', 'Relay name must be nonblank and at most 256 UTF-8 bytes.');
    requireSafeInteger(info.limits.messageRetention, 1);
    for (const value of [info.limits.channelTimelineRetention, info.limits.groupMessageRetention, info.limits.maxGroupMembers, info.limits.maxGroupInviteTtl])
        if (value !== undefined) requireSafeInteger(value, 1);
    for (const value of [info.limits.maxChannelSubscriptions, info.limits.maxGroupSubscriptions]) if (value !== undefined) requireSafeInteger(value, 0);
    if (descriptor) {
        if (info.relayId !== descriptor.relayId) throw new ProtocolError('invalid_identity', 'Relay info belongs to a different relay.');
        const sockets = descriptor.endpoints.some(endpoint => endpoint.startsWith('wss://'));
        const limits = info.limits;
        if (descriptor.capabilities?.includes('channel.host.v1') && (limits.channelTimelineRetention === undefined || sockets && limits.maxChannelSubscriptions === undefined)
            || descriptor.capabilities?.includes('group.host.v1') && (limits.groupMessageRetention === undefined || limits.maxGroupMembers === undefined
                || limits.maxGroupInviteTtl === undefined || sockets && limits.maxGroupSubscriptions === undefined))
            throw new ProtocolError('missing_limits', 'Relay info omits limits required by its advertised capabilities.');
    }
}
import { validateCompleteObject } from '../protocol/validation.js';
import { parseAbsoluteUrl } from '../runtime/url.js';
