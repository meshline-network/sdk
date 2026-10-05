import { ProtocolError } from '../errors.js';
import { validateRelayId } from '../identity/neo.js';
import type { RelayEntry, RelayRegistry } from '../interactions.js';
import type { NetworkContext } from '../protocol/context.js';
import { decodeUtf8 } from '../protocol/encoding.js';
import { parseJson } from '../protocol/json.js';
import { abortScope, throwIfAborted } from '../runtime/clock.js';
import { parseAbsoluteUrl } from '../runtime/url.js';
import { webEndpoint } from '../transport/endpoint.js';
import type { RelayFetch } from '../transport/http.js';
import { array, base64, object, text } from './neo-json.js';

/** Explicit, application-owned RPC configuration; the SDK never selects a Registry implementation automatically. */
export interface RpcRelayRegistryOptions {
    readonly context: NetworkContext;
    readonly rpcUrl: string;
    readonly fetch?: RelayFetch;
    /** Per-request timeout, including response body reads; defaults to 15000 ms. */
    readonly requestTimeoutMilliseconds?: number;
    /** Entries per RPC iterator page, from 1 to 1000; defaults to 100. */
    readonly iteratorPageSize?: number;
    /** Cleanup errors are reported without replacing the query failure or caller cancellation. Defaults to console.warn. */
    readonly onSessionCleanupError?: (error: unknown) => void;
}

/** Read-only Neo N3 Registry ABI over JSON-RPC, including iterator sessions and network verification. */
export class RpcRelayRegistry implements RelayRegistry {
    readonly context: NetworkContext;
    readonly #url: string;
    readonly #fetch: RelayFetch;
    readonly #timeout: number;
    readonly #pageSize: number;
    readonly #cleanupError: (error: unknown) => void;
    #requestId = 0;

    constructor(options: RpcRelayRegistryOptions) {
        const url = parseAbsoluteUrl(options.rpcUrl);
        if (!['http:', 'https:'].includes(url.protocol) || !url.hostname || url.username || url.password || url.hash)
            throw new ProtocolError('invalid_rpc_url', 'Expected an absolute HTTP(S) RPC URL without user information or a fragment.');
        this.context = options.context;
        this.#url = url.href;
        this.#fetch = options.fetch ?? ((address, init) => fetch(address, init));
        this.#timeout = options.requestTimeoutMilliseconds ?? 15_000;
        this.#pageSize = options.iteratorPageSize ?? 100;
        if (!Number.isSafeInteger(this.#timeout) || this.#timeout < 1 || this.#timeout > 2_147_483_647) throw new RangeError('Invalid RPC timeout.');
        if (!Number.isInteger(this.#pageSize) || this.#pageSize < 1 || this.#pageSize > 1000) throw new RangeError('Invalid iterator page size.');
        this.#cleanupError = options.onSessionCleanupError ?? (error => console.warn('Could not release Neo Registry iterator session.', error));
    }

    async getRelay(relayId: string, signal?: AbortSignal): Promise<RelayEntry | undefined> {
        validateRelayId(relayId);
        await this.#verifyNetwork(signal);
        const item = stackItem(await this.#call('invokefunction', [this.context.registry, 'getRelay', [{ type: 'Hash160', value: relayId }]], signal));
        if (item['type'] === 'Any' && item['value'] == null) return undefined;
        const entry = readEntry(item);
        if (entry.relayId !== relayId) throw new ProtocolError('invalid_registry', 'Registry returned another relay record.');
        return entry;
    }

    async *getRelays(signal?: AbortSignal): AsyncIterable<RelayEntry> {
        await this.#verifyNetwork(signal);
        const result = object(await this.#call('invokefunction', [this.context.registry, 'listRelays', []], signal));
        const session = result['session'] === undefined ? undefined : text(result['session']);
        try {
            const iterator = stackItem(result);
            if (iterator['type'] !== 'InteropInterface') throw new ProtocolError('invalid_registry', 'Registry listRelays did not return an iterator.');
            if (session !== undefined) {
                const iteratorId = text(iterator['id']);
                for (;;) {
                    const batch = array(await this.#call('traverseiterator', [session, iteratorId, this.#pageSize], signal));
                    if (batch.length > this.#pageSize) throw new ProtocolError('invalid_registry', 'Oversized iterator page.');
                    for (const item of batch) { throwIfAborted(signal); yield readEntry(item); }
                    if (batch.length === 0) break;
                }
            } else {
                if (iterator['truncated'] !== false) throw new ProtocolError('invalid_registry', 'Inline iterator may be truncated; enable RPC iterator sessions.');
                for (const item of array(iterator['iterator'])) { throwIfAborted(signal); yield readEntry(item); }
            }
        } finally {
            if (session !== undefined) {
                try {
                    // Cleanup has its own bounded deadline, independent of a canceled consumer.
                    if (await this.#call('terminatesession', [session]) !== true)
                        throw new ProtocolError('registry_cleanup_failed', 'RPC did not confirm session termination.');
                } catch (error) { this.#cleanupError(error); }
            }
        }
    }

    async #verifyNetwork(signal?: AbortSignal): Promise<void> {
        const version = object(await this.#call('getversion', [], signal));
        if (object(version['protocol'])['network'] !== this.context.reference)
            throw new ProtocolError('network_mismatch', 'RPC network magic differs from the configured Meshline network.');
    }

    async #call(method: string, parameters: unknown[], signal?: AbortSignal): Promise<unknown> {
        const deadline = abortScope([signal], this.#timeout, `registry.${method}`);
        try {
            throwIfAborted(deadline.signal);
            const id = ++this.#requestId;
            const response = await this.#fetch(this.#url, { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id, method, params: parameters }), signal: deadline.signal,
                redirect: 'error', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer' });
            if (response.status < 200 || response.status >= 300 || response.redirected || response.type === 'opaqueredirect') {
                await response.body?.cancel();
                throw new ProtocolError('rpc_http_error', `Neo RPC returned HTTP ${response.status}.`);
            }
            const contentLength = response.headers.get('content-length');
            if (contentLength !== null && Number(contentLength) > 1024 * 1024) {
                await response.body?.cancel();
                throw new ProtocolError('rpc_response_too_large', 'Neo RPC response exceeds 1 MiB.');
            }
            const bytes = new Uint8Array(await response.arrayBuffer());
            throwIfAborted(deadline.signal);
            if (bytes.length > 1024 * 1024) throw new ProtocolError('rpc_response_too_large', 'Neo RPC response exceeds 1 MiB.');
            const root = object(parseJson(decodeUtf8(bytes)));
            if (root['jsonrpc'] !== '2.0' || root['id'] !== id || Object.hasOwn(root, 'result') === Object.hasOwn(root, 'error'))
                throw new ProtocolError('invalid_rpc_response', 'Mismatched JSON-RPC response.');
            if (Object.hasOwn(root, 'error')) throw new ProtocolError('rpc_error', `Neo RPC ${method} failed: ${JSON.stringify(root['error'])}`);
            return root['result'];
        } catch (error) { throw deadline.normalizeError(error); }
        finally { deadline.dispose(); }
    }
}

function stackItem(value: unknown): Record<string, unknown> {
    const result = object(value);
    if (result['state'] !== 'HALT') throw new ProtocolError('registry_vm_fault', 'Registry invocation did not halt successfully.');
    const stack = array(result['stack']);
    if (stack.length !== 1) throw new ProtocolError('invalid_registry', 'Expected exactly one stack item.');
    return object(stack[0]);
}

function vmBytes(value: unknown): Uint8Array {
    const item = object(value);
    if (item['type'] !== 'ByteString' && item['type'] !== 'Buffer') throw new ProtocolError('invalid_registry', 'Expected a Neo VM byte string.');
    return base64(item['value']);
}

function readEntry(value: unknown): RelayEntry {
    const item = object(value);
    if (item['type'] !== 'Array' && item['type'] !== 'Struct') throw new ProtocolError('invalid_registry', 'Expected a Registry RelayEntry array.');
    const fields = array(item['value']);
    if (fields.length !== 4) throw new ProtocolError('invalid_registry', 'Expected four RelayEntry fields.');
    const hash = vmBytes(fields[0]);
    if (hash.length !== 20) throw new ProtocolError('invalid_registry', 'Expected a 20-byte relay hash.');
    const relayId = '0x' + Array.from(hash.reverse(), byte => byte.toString(16).padStart(2, '0')).join('');
    const endpoint = decodeUtf8(vmBytes(fields[1]));
    webEndpoint(endpoint, 'https:');
    const status = decodeUtf8(vmBytes(fields[2]));
    if (!['active', 'disabled', 'suspended'].includes(status)) throw new ProtocolError('invalid_registry', 'Unknown relay status.');
    const timestamp = object(fields[3]);
    const integer = text(timestamp['value']);
    if (timestamp['type'] !== 'Integer' || !/^[0-9]+$/.test(integer) || integer.length > 20)
        throw new ProtocolError('invalid_registry', 'Expected an unsigned millisecond timestamp.');
    const updatedAt = BigInt(integer);
    if (updatedAt > 0xffff_ffff_ffff_ffffn) throw new ProtocolError('invalid_registry', 'Registry timestamp exceeds UInt64.');
    return { relayId, endpoint, status: status as RelayEntry['status'], updatedAt };
}
