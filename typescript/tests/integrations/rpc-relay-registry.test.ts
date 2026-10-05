import { describe, expect, it } from 'vitest';
import { RpcRelayRegistry, NetworkContext, type RelayFetch, type RelayEntry } from '@meshline/sdk';

const context = new NetworkContext(12345, '0x0123456789012345678901234567890123456789');
const relayId = '0x0102030405060708091011121314151617181920';
const invocation = (item: unknown): unknown => ({ state: 'HALT', stack: [item] });
const entry = (status = 'active', timestamp = '1730000000000'): unknown => ({ type: 'Array', value: [
    { type: 'ByteString', value: Buffer.from(relayId.slice(2), 'hex').reverse().toString('base64') },
    { type: 'ByteString', value: Buffer.from('https://relay.test/meshline/v1').toString('base64') },
    { type: 'ByteString', value: Buffer.from(status).toString('base64') },
    { type: 'Integer', value: timestamp },
] });
function registry(respond: (method: string, parameters: unknown[], signal: AbortSignal) => unknown, cleanupErrors: unknown[] = []): RpcRelayRegistry {
    const fetch: RelayFetch = async (_, init) => {
        const request = JSON.parse(init.body!) as { id: number; method: string; params: unknown[] };
        const result: unknown = respond(request.method, request.params, init.signal);
        return new Response(JSON.stringify({ jsonrpc: '2.0', id: request.id, result }));
    };
    return new RpcRelayRegistry({ context, rpcUrl: 'https://neo.test', iteratorPageSize: 2, fetch, onSessionCleanupError: error => cleanupErrors.push(error) });
}
async function collect(source: AsyncIterable<RelayEntry>): Promise<RelayEntry[]> {
    const entries: RelayEntry[] = [];
    for await (const entry of source) entries.push(entry);
    return entries;
}

describe('Neo RPC Registry', () => {
    it('checks the network, encodes contract arguments and preserves UInt64 precision', async () => {
        const methods: string[] = [];
        const rpc = registry((method, parameters) => {
            methods.push(method);
            if (method === 'getversion') return { protocol: { network: 12345 } };
            expect(parameters).toEqual([context.registry, 'getRelay', [{ type: 'Hash160', value: relayId }]]);
            return invocation(entry('suspended', '18446744073709551615'));
        });
        expect(await rpc.getRelay(relayId)).toEqual({ relayId, endpoint: 'https://relay.test/meshline/v1', status: 'suspended', updatedAt: 18446744073709551615n });
        expect(methods).toEqual(['getversion', 'invokefunction']);
    });

    it('returns undefined only for an unknown registration', async () => {
        const rpc = registry(method => method === 'getversion' ? { protocol: { network: 12345 } } : invocation({ type: 'Any', value: null }));
        expect(await rpc.getRelay(relayId)).toBeUndefined();
    });

    it('rejects a network mismatch before invoking the contract', async () => {
        const rpc = registry(method => { expect(method).toBe('getversion'); return { protocol: { network: 1 } }; });
        await expect(rpc.getRelay(relayId)).rejects.toThrow('network magic');
    });

    it.each(['fault', 'wrong-relay', 'status', 'timestamp', 'shape'])('rejects invalid Registry data: %s', async failure => {
        const rpc = registry(method => {
            if (method === 'getversion') return { protocol: { network: 12345 } };
            if (failure === 'fault') return { state: 'FAULT', stack: [] };
            if (failure === 'shape') return invocation({ type: 'Struct', value: [] });
            return invocation(entry(failure === 'status' ? 'unknown' : 'active', failure === 'timestamp' ? '18446744073709551616' : '1'));
        });
        await expect(rpc.getRelay(failure === 'wrong-relay' ? '0x0000000000000000000000000000000000000000' : relayId)).rejects.toThrow();
    });

    it.each(['complete', 'early', 'cancel', 'malformed'])('releases iterator sessions after %s enumeration', async mode => {
        const calls: string[] = [], controller = new AbortController();
        let page = 0;
        const rpc = registry((method, parameters, signal) => {
            calls.push(method);
            if (method === 'getversion') return { protocol: { network: 12345 } };
            if (method === 'invokefunction') return { state: 'HALT', session: 's', stack: [{ type: 'InteropInterface', id: 'i' }] };
            if (method === 'traverseiterator') {
                expect(parameters).toEqual(['s', 'i', 2]);
                if (mode === 'malformed') return [entry('unknown')];
                return ++page === 1 ? [entry('disabled'), entry('suspended')] : [];
            }
            expect(method).toBe('terminatesession');
            expect(signal.aborted).toBe(false);
            return true;
        });
        if (mode === 'complete') expect((await collect(rpc.getRelays())).map(value => value.status)).toEqual(['disabled', 'suspended']);
        else if (mode === 'early') { for await (const _ of rpc.getRelays()) break; }
        else if (mode === 'malformed') await expect(collect(rpc.getRelays())).rejects.toThrow();
        else {
            const iterator = rpc.getRelays(controller.signal)[Symbol.asyncIterator]();
            await iterator.next();
            controller.abort();
            await expect(iterator.next()).rejects.toThrow();
        }
        expect(calls.at(-1)).toBe('terminatesession');
    });

    it.each([true, false])('accepts only complete inline results (truncated=%s)', async truncated => {
        const rpc = registry(method => method === 'getversion' ? { protocol: { network: 12345 } }
            : invocation({ type: 'InteropInterface', iterator: [entry()], truncated }));
        if (truncated) await expect(collect(rpc.getRelays())).rejects.toThrow('truncated');
        else expect(await collect(rpc.getRelays())).toHaveLength(1);
    });

    it('reports cleanup failure without hiding the primary enumeration failure', async () => {
        const errors: unknown[] = [];
        const rpc = registry(method => {
            if (method === 'getversion') return { protocol: { network: 12345 } };
            if (method === 'invokefunction') return { state: 'HALT', session: 's', stack: [{ type: 'InteropInterface', id: 'i' }] };
            if (method === 'traverseiterator') return [entry('unknown')];
            throw new Error('cleanup failed');
        }, errors);
        await expect(collect(rpc.getRelays())).rejects.toThrow('Unknown relay status');
        expect(errors).toHaveLength(1);
    });

    it.each(['rpc', 'id', 'missing', 'http'])('propagates %s response errors', async failure => {
        const fetch: RelayFetch = async () => new Response(JSON.stringify(failure === 'rpc'
            ? { jsonrpc: '2.0', id: 1, error: { code: -100, message: 'RPC unavailable' } }
            : failure === 'id' ? { jsonrpc: '2.0', id: 0, result: {} } : {}), { status: failure === 'http' ? 503 : 200 });
        const rpc = new RpcRelayRegistry({ context, rpcUrl: 'https://neo.test', fetch });
        await expect(rpc.getRelay(relayId)).rejects.toThrow();
    });

    it('cancels RPC I/O at the request deadline', async () => {
        const fetch: RelayFetch = (_, init) => new Promise((_, reject) => init.signal.addEventListener('abort', () => reject(init.signal.reason), { once: true }));
        const rpc = new RpcRelayRegistry({ context, rpcUrl: 'https://neo.test', fetch, requestTimeoutMilliseconds: 10 });
        await expect(rpc.getRelay(relayId)).rejects.toThrow('timed out');
    });
});
