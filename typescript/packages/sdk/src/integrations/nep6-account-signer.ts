import { ecb } from '@noble/ciphers/aes.js';
import { scryptAsync } from '@noble/hashes/scrypt.js';
import { accountPublicKey, sha256, signAccount, systemRandom, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { getAccountId } from '../identity/neo.js';
import type { AccountSigner } from '../interactions.js';
import type { NetworkContext } from '../protocol/context.js';
import { concatBytes, encodeUtf8, equalBytes } from '../protocol/encoding.js';
import { parseJson } from '../protocol/json.js';
import { throwIfAborted } from '../runtime/clock.js';
import { array, base64, object, text } from './neo-json.js';

export interface Nep6AccountSignerOptions {
    readonly context: NetworkContext;
    /** Zero-based wallet file order, default 0. isDefault never overrides this selection. */
    readonly accountIndex?: number;
    /** Supply a native cryptographic source on platforms without crypto.getRandomValues. */
    readonly random?: RandomSource;
}

/** Neo N3 single-signature wallet account. The application supplies JSON, so browser/Expo code needs no filesystem API. */
export class Nep6AccountSigner implements AccountSigner {
    readonly accountId: string;
    readonly address: string;
    readonly #publicKey: Uint8Array;
    readonly #privateKey: Uint8Array;
    readonly #random: RandomSource;
    #disposed = false;

    private constructor(privateKey: Uint8Array, context: NetworkContext, random: RandomSource) {
        this.#publicKey = accountPublicKey(privateKey);
        this.accountId = getAccountId(`neo:${context.reference}`, this.#publicKey);
        this.address = this.accountId.slice(this.accountId.lastIndexOf(':') + 1);
        this.#random = random;
        this.#privateKey = privateKey.slice();
    }

    /** Returns a copy, preventing callers from changing the advertised identity. */
    get publicKey(): Uint8Array { return this.#publicKey.slice(); }

    /** Decrypts NEP-2 using the wallet's scrypt parameters. Never modifies the wallet or chooses another account on failure. */
    static async fromJson(walletJson: string, password: string, options: Nep6AccountSignerOptions, signal?: AbortSignal): Promise<Nep6AccountSigner> {
        throwIfAborted(signal);
        const wallet = object(parseJson(walletJson));
        if (wallet['version'] !== '1.0') throw new ProtocolError('invalid_wallet', 'Expected a NEP-6 version 1.0 wallet.');
        const accounts = array(wallet['accounts']);
        const index = options.accountIndex ?? 0;
        if (!Number.isInteger(index) || index < 0 || index >= accounts.length) throw new RangeError('The selected wallet account does not exist.');
        const account = object(accounts[index]);
        const encrypted = text(account['key']);
        const address = text(account['address']);
        const contract = object(account['contract']);
        const parameters = array(contract['parameters']);
        if (contract['deployed'] !== false || parameters.length !== 1 || object(parameters[0])['type'] !== 'Signature')
            throw new ProtocolError('invalid_wallet', 'Expected a Neo N3 standard single-signature account.');
        const script = base64(contract['script']);
        const costs = object(wallet['scrypt']);
        const n = costs['n'], r = costs['r'], p = costs['p'];
        if (typeof n !== 'number' || typeof r !== 'number' || typeof p !== 'number'
            || !Number.isSafeInteger(n) || !Number.isSafeInteger(r) || !Number.isSafeInteger(p)
            || n < 2 || n > 2 ** 30 || (n & (n - 1)) !== 0 || r < 1 || p < 1
            || 128 * r * (n + p + 2) > 256 * 1024 * 1024 || n * r * p > 16_777_216)
            throw new ProtocolError('invalid_wallet', 'Unsupported NEP-6 scrypt parameters (256 MiB memory / 16777216 work limit).');
        const bytes = decodeNep2(encrypted);
        const passwordBytes = encodeUtf8(password.normalize('NFC'));
        let derived: Uint8Array | undefined;
        let privateKey: Uint8Array | undefined;
        let signer: Nep6AccountSigner | undefined;
        try {
            derived = await scryptAsync(passwordBytes, bytes.subarray(3, 7), { N: n, r, p, dkLen: 64, maxmem: 256 * 1024 * 1024,
                onProgress: () => throwIfAborted(signal) });
            throwIfAborted(signal);
            privateKey = ecb(derived.subarray(32), { disablePadding: true }).decrypt(bytes.subarray(7, 39));
            for (let i = 0; i < privateKey.length; i++) privateKey[i] = privateKey[i]! ^ derived[i]!;
            signer = new Nep6AccountSigner(privateKey, options.context, options.random ?? systemRandom);
            if (!equalBytes(bytes.subarray(3, 7), sha256(sha256(encodeUtf8(signer.address))).subarray(0, 4)))
                throw new ProtocolError('wallet_decryption_failed', 'The wallet password or encrypted key is incorrect.');
            const expectedScript = concatBytes(new Uint8Array([0x0c, 0x21]), signer.#publicKey, new Uint8Array([0x41, 0x56, 0xe7, 0xb3, 0x27]));
            if (address !== signer.address || !equalBytes(script, expectedScript))
                throw new ProtocolError('invalid_wallet', 'The account address and standard contract must match its private key.');
            return signer;
        } catch (error) { signer?.dispose(); throw error; }
        finally { passwordBytes.fill(0); derived?.fill(0); privateKey?.fill(0); }
    }

    async sign(input: Uint8Array, signal?: AbortSignal): Promise<Uint8Array> {
        if (this.#disposed) throw new ProtocolError('signer_disposed', 'The wallet signer has been disposed.');
        throwIfAborted(signal);
        return signAccount(input, this.#privateKey, this.#random);
    }

    /** Best-effort clearing of owned private-key bytes; JavaScript runtimes cannot guarantee erasure of all internal copies. */
    dispose(): void { this.#privateKey.fill(0); this.#disposed = true; }
}

function decodeNep2(encrypted: string): Uint8Array {
    const alphabet = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
    if (encrypted.length !== 58) throw new ProtocolError('invalid_wallet', 'Invalid NEP-2 key length.');
    let value = 0n;
    for (const character of encrypted) {
        const digit = alphabet.indexOf(character);
        if (digit < 0) throw new ProtocolError('invalid_wallet', 'Invalid NEP-2 Base58 character.');
        value = value * 58n + BigInt(digit);
    }
    const bytes = new Uint8Array(43);
    for (let i = 42; i >= 0; i--) { bytes[i] = Number(value & 255n); value >>= 8n; }
    if (value !== 0n || bytes[0] !== 1 || bytes[1] !== 0x42 || bytes[2] !== 0xe0
        || !equalBytes(bytes.subarray(39), sha256(sha256(bytes.subarray(0, 39))).subarray(0, 4)))
        throw new ProtocolError('invalid_wallet', 'Invalid NEP-2 prefix or checksum.');
    return bytes;
}
