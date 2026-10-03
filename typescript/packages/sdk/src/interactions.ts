import type { DeviceCertificate } from './models/identity.js';
import type { NetworkContext } from './protocol/context.js';

/** The application retains its account private key and signs the exact input using Neo P-256/SHA-256. */
export interface AccountSigner {
    readonly accountId: string;
    readonly publicKey: Uint8Array;
    sign(input: Uint8Array, signal?: AbortSignal): Promise<Uint8Array>;
}

/** Device signatures are Ed25519 over the exact supplied bytes. */
export interface DeviceSigner {
    readonly certificate: DeviceCertificate;
    sign(input: Uint8Array, signal?: AbortSignal): Promise<Uint8Array>;
}

export interface RelayEntry {
    readonly relayId: string;
    readonly endpoint: string;
    readonly status: 'active' | 'disabled' | 'suspended';
    /** Registry UInt64 timestamp, in Unix milliseconds; this is not a protocol JSON number. */
    readonly updatedAt: bigint;
}

export interface RelayRegistry {
    readonly context: NetworkContext;
    getRelay(relayId: string, signal?: AbortSignal): Promise<RelayEntry | undefined>;
    getRelays(signal?: AbortSignal): AsyncIterable<RelayEntry>;
}

/** Use OS-backed protection in the application adapter; never persist unprotected device secrets. */
export interface SecretProtector {
    protect(plaintext: Uint8Array, purpose: string, signal?: AbortSignal): Promise<Uint8Array>;
    unprotect(protectedData: Uint8Array, purpose: string, signal?: AbortSignal): Promise<Uint8Array>;
}
