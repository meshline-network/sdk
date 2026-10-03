import { sha256, requireLength, systemRandom, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { NetworkContext, signingInput } from '../protocol/context.js';
import { decodeBase64Url, encodeBase64Url } from '../protocol/encoding.js';
import { validateAccountId, validateRelayId } from './neo.js';

export type IdentifierKind = 'device' | 'message' | 'channel' | 'group' | 'invite';
const prefixes: Record<IdentifierKind, string> = { device: 'dev_', message: 'msg_', channel: 'chan_', group: 'grp_', invite: 'inv_' };

/** Checks canonical representation only, not existence or authorization. */
export function validateIdentifier(kind: IdentifierKind, value: string): void {
    const prefix = prefixes[kind];
    if (!value.startsWith(prefix)) throw new ProtocolError('invalid_identifier', `Expected ${prefix} identifier.`);
    decodeBase64Url(value.slice(prefix.length), 16);
}

export function createIdentifier(kind: 'message' | 'invite', random: RandomSource = systemRandom): string {
    const bytes = random.bytes(16);
    requireLength(bytes, 16, 'Identifier randomness');
    return prefixes[kind] + encodeBase64Url(bytes);
}

export function deriveDeviceId(account: string, signingPublicKey: Uint8Array, encryptionPublicKey: Uint8Array, context: NetworkContext): string {
    validateAccountId(account);
    requireLength(signingPublicKey, 32, 'Signing public key');
    requireLength(encryptionPublicKey, 32, 'Encryption public key');
    const input = signingInput({
        $type: 'meshline.device.identity', account,
        signing_public_key: encodeBase64Url(signingPublicKey),
        encryption_public_key: encodeBase64Url(encryptionPublicKey),
    }, context);
    return 'dev_' + encodeBase64Url(sha256(input).subarray(0, 16));
}

export function deriveResourceId(kind: 'channel' | 'group', creator: string, relayId: string, nonce: Uint8Array, context: NetworkContext): string {
    validateAccountId(creator);
    validateRelayId(relayId);
    requireLength(nonce, 16, 'Resource nonce');
    const input = signingInput({ $type: `meshline.${kind}.identity`, creator, relay_id: relayId, nonce: encodeBase64Url(nonce) }, context);
    return prefixes[kind] + encodeBase64Url(sha256(input).subarray(0, 16));
}
