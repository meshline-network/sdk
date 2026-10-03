import { ed25519, x25519 } from '@noble/curves/ed25519.js';
import { p256 } from '@noble/curves/nist.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { ripemd160 } from '@noble/hashes/legacy.js';
import { hkdf } from '@noble/hashes/hkdf.js';
import { gcm } from '@noble/ciphers/aes.js';
import { ProtocolError } from '../errors.js';

export { sha256 };

/** Platform cryptographic random source; implementations must never use Math.random. */
export interface RandomSource { bytes(length: number): Uint8Array }

/** Browser and Node.js random source. Expo supplies an explicit native source. */
export const systemRandom: RandomSource = {
    bytes(length) {
        if (!Number.isSafeInteger(length) || length < 0) throw new RangeError('Invalid random byte count.');
        if (!globalThis.crypto?.getRandomValues)
            throw new ProtocolError('random_unavailable', 'A cryptographic random source is required.');
        const result = new Uint8Array(length);
        for (let offset = 0; offset < length; offset += 65_536)
            globalThis.crypto.getRandomValues(result.subarray(offset, offset + 65_536));
        return result;
    },
};

export function requireLength(value: Uint8Array, length: number, name: string): void {
    if (!(value instanceof Uint8Array) || value.length !== length)
        throw new ProtocolError('invalid_length', `${name} must contain ${length} bytes.`);
}

/** Ed25519 public key derived from a 32-byte seed. */
export function devicePublicKey(seed: Uint8Array): Uint8Array {
    requireLength(seed, 32, 'Ed25519 seed');
    return ed25519.getPublicKey(seed);
}

/** Signs the exact supplied bytes with Ed25519. */
export function signDevice(input: Uint8Array, seed: Uint8Array): Uint8Array {
    requireLength(seed, 32, 'Ed25519 seed');
    return ed25519.sign(input, seed);
}

/** Uses strict RFC 8032 verification, not permissive ZIP215 acceptance. */
export function verifyDevice(input: Uint8Array, signature: Uint8Array, publicKey: Uint8Array): boolean {
    if (signature.length !== 64 || publicKey.length !== 32) return false;
    return ed25519.verify(signature, input, publicKey, { zip215: false });
}

/** Compressed P-256 public key, in the format used by Neo N3 accounts. */
export function accountPublicKey(privateKey: Uint8Array): Uint8Array {
    requireLength(privateKey, 32, 'P-256 private key');
    return p256.getPublicKey(privateKey, true);
}

/** Verifies SHA-256 / P-256 compact signatures; both valid high-S and low-S forms are accepted. */
export function verifyAccount(input: Uint8Array, signature: Uint8Array, publicKey: Uint8Array): boolean {
    if (signature.length !== 64 || publicKey.length !== 33 || (publicKey[0] !== 2 && publicKey[0] !== 3)) return false;
    return p256.verify(signature, input, publicKey, { prehash: true, lowS: false, format: 'compact' });
}

/** Low-level signing helper; application signers retain ownership of account keys. */
export function signAccount(input: Uint8Array, privateKey: Uint8Array, random: RandomSource = systemRandom): Uint8Array {
    requireLength(privateKey, 32, 'P-256 private key');
    const entropy = random.bytes(32);
    requireLength(entropy, 32, 'Signing entropy');
    try { return p256.sign(input, privateKey, { prehash: true, lowS: false, format: 'compact', extraEntropy: entropy }); }
    finally { entropy.fill(0); }
}

export function encryptionPublicKey(privateKey: Uint8Array): Uint8Array {
    requireLength(privateKey, 32, 'X25519 private key');
    return x25519.getPublicKey(privateKey);
}

export function agreeKey(privateKey: Uint8Array, publicKey: Uint8Array): Uint8Array {
    requireLength(privateKey, 32, 'X25519 private key');
    requireLength(publicKey, 32, 'X25519 public key');
    try { return x25519.getSharedSecret(privateKey, publicKey); }
    catch (cause) { throw new ProtocolError('invalid_key', 'X25519 agreement failed, including an all-zero shared secret.', { cause }); }
}

export function hash160(value: Uint8Array): Uint8Array { return ripemd160(sha256(value)); }

export function deriveKey(secret: Uint8Array, salt: Uint8Array, info: Uint8Array, length = 32): Uint8Array {
    return hkdf(sha256, secret, salt, info, length);
}

/** AES-256-GCM ciphertext followed by a 16-byte tag. The caller guarantees nonce uniqueness. */
export function encryptAes(key: Uint8Array, nonce: Uint8Array, plaintext: Uint8Array, aad: Uint8Array): Uint8Array {
    requireLength(key, 32, 'AES-256 key');
    requireLength(nonce, 12, 'GCM nonce');
    return gcm(key, nonce, aad).encrypt(plaintext);
}

/** Authenticates before exposing plaintext; malformed ciphertext is a protocol failure. */
export function decryptAes(key: Uint8Array, nonce: Uint8Array, ciphertext: Uint8Array, aad: Uint8Array): Uint8Array {
    requireLength(key, 32, 'AES-256 key');
    requireLength(nonce, 12, 'GCM nonce');
    if (ciphertext.length < 16) throw new ProtocolError('invalid_ciphertext', 'Missing GCM tag.');
    try { return gcm(key, nonce, aad).decrypt(ciphertext); }
    catch (cause) { throw new ProtocolError('invalid_ciphertext', 'GCM authentication failed.', { cause }); }
}
