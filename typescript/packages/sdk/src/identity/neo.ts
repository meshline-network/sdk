import { p256 } from '@noble/curves/nist.js';
import { hash160, sha256, verifyAccount } from '../crypto/primitives.js';
import { concatBytes, equalBytes } from '../protocol/encoding.js';
import { ProtocolError } from '../errors.js';

const base58 = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';

function scriptHash(publicKey: Uint8Array): Uint8Array {
    if (publicKey.length !== 33 || (publicKey[0] !== 2 && publicKey[0] !== 3))
        throw new ProtocolError('invalid_key', 'Expected a compressed P-256 key.');
    try { p256.Point.fromBytes(publicKey).assertValidity(); }
    catch (cause) { throw new ProtocolError('invalid_key', 'The account key is not a valid P-256 point.', { cause }); }
    return hash160(concatBytes(new Uint8Array([0x0c, 0x21]), publicKey, new Uint8Array([0x41, 0x56, 0xe7, 0xb3, 0x27])));
}

function chainReference(chainId: string): number {
    const match = /^neo:(0|[1-9][0-9]{0,9})$/.exec(chainId);
    if (!match || match[0] !== chainId || Number(match[1]) > 0xffff_ffff)
        throw new ProtocolError('invalid_account', 'Expected a canonical Neo chain identifier.');
    return Number(match[1]);
}

/** Derives a canonical CAIP-10 Neo N3 account identifier. */
export function getAccountId(chainId: string, publicKey: Uint8Array): string {
    chainReference(chainId);
    const payload = concatBytes(new Uint8Array([0x35]), scriptHash(publicKey));
    const encoded = concatBytes(payload, sha256(sha256(payload)).subarray(0, 4));
    let integer = 0n;
    for (const byte of encoded) integer = (integer << 8n) | BigInt(byte);
    let address = '';
    while (integer > 0n) { address = base58[Number(integer % 58n)]! + address; integer /= 58n; }
    return `${chainId}:${address}`;
}

/** Checks account format and address checksum without normalizing the input. */
export function validateAccountId(accountId: string): void {
    const split = accountId.lastIndexOf(':');
    chainReference(accountId.slice(0, split));
    const address = accountId.slice(split + 1);
    if (address.length !== 34) throw new ProtocolError('invalid_account', 'A Neo N3 address contains 34 characters.');
    let integer = 0n;
    for (const character of address) {
        const digit = base58.indexOf(character);
        if (digit < 0) throw new ProtocolError('invalid_account', 'Invalid Base58 address character.');
        integer = integer * 58n + BigInt(digit);
    }
    const payload = new Uint8Array(25);
    for (let index = 24; index >= 0; index--) { payload[index] = Number(integer & 255n); integer >>= 8n; }
    if (integer !== 0n || payload[0] !== 0x35 || !equalBytes(payload.subarray(21), sha256(sha256(payload.subarray(0, 21))).subarray(0, 4)))
        throw new ProtocolError('invalid_account', 'Invalid Neo address version, length, or checksum.');
}

/** Verifies that the public key identifies the account, including its chain reference. */
export function matchesAccount(accountId: string, publicKey: Uint8Array): boolean {
    validateAccountId(accountId);
    return getAccountId(accountId.slice(0, accountId.lastIndexOf(':')), publicKey) === accountId;
}

/** Derives a lowercase display-order Neo script hash for a relay. */
export function getRelayId(publicKey: Uint8Array): string {
    return '0x' + Array.from(scriptHash(publicKey).reverse(), byte => byte.toString(16).padStart(2, '0')).join('');
}

export function validateRelayId(relayId: string): void {
    if (!/^0x[0-9a-f]{40}$/.test(relayId) || relayId.length !== 42)
        throw new ProtocolError('invalid_relay', 'Expected a canonical lowercase Neo script hash.');
}

export function verifyRelay(relayId: string, publicKey: Uint8Array, input: Uint8Array, signature: Uint8Array): boolean {
    validateRelayId(relayId);
    return verifyAccount(input, signature, publicKey) && getRelayId(publicKey) === relayId;
}
