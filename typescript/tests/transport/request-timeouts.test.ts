import { expect, test } from 'vitest';
import { HttpRelayTransport, RpcRelayRegistry, NetworkContext, type RelayFetch } from '@meshline/sdk';

test.each([
    ['relay', 'headers'], ['relay', 'body'], ['registry', 'headers'], ['registry', 'body'],
])('%s %s timeout preserves context when the adapter returns a generic abort error', async (transport, stage) => {
    let notify!: () => void;
    const entered = new Promise<void>(resolve => { notify = resolve; });
    const original = new DOMException('Native I/O was aborted.', 'AbortError');
    const fetch: RelayFetch = async (_, init) => {
        if (stage === 'headers') return new Promise((_, reject) => {
            init.signal.addEventListener('abort', () => reject(original), { once: true });
            notify();
        });
        return new Response(new ReadableStream({ start(controller) {
            init.signal.addEventListener('abort', () => controller.error(original), { once: true });
            notify();
        } }), { headers: { 'content-type': 'application/json' } });
    };
    const relay = new HttpRelayTransport({ fetch, requestTimeoutMilliseconds: 20 });
    const registry = new RpcRelayRegistry({ context: new NetworkContext(12345, '0x0123456789012345678901234567890123456789'),
        rpcUrl: 'https://neo.test', fetch, requestTimeoutMilliseconds: 20 });
    const operation = transport === 'relay' ? 'relay.http.probe' : 'registry.getversion';
    const run = (signal?: AbortSignal) => transport === 'relay'
        ? relay.request('https://relay.test/meshline/v1', 'GET', 'probe', undefined, undefined, signal)
        : registry.getRelay('0x0102030405060708091011121314151617181920', signal);
    try {
        const failure = expect(run()).rejects.toMatchObject({ name: 'TimeoutError', operation, timeoutMilliseconds: 20, cause: original });
        await entered;
        await failure;

        // The same generic adapter error must retain an explicit caller's reason.
        const caller = new AbortController();
        let started!: () => void;
        const ready = new Promise<void>(resolve => { started = resolve; });
        notify = started;
        // Even a frozen caller-owned TimeoutError must pass through unchanged.
        const canceled = Object.freeze(new DOMException('Caller stopped.', 'TimeoutError'));
        const result = expect(run(caller.signal)).rejects.toBe(canceled);
        await ready;
        caller.abort(canceled);
        await result;
    } finally { relay.dispose(); }
});
