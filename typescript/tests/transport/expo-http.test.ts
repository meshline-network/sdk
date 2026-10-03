import { expect, test } from 'vitest';
import { HttpRelayTransport, type RelayFetchInit } from '@meshline/sdk';
import { createExpoRelayFetch } from '../../packages/expo/src/http.js';

const init = (signal: AbortSignal): RelayFetchInit => ({ method: 'GET', headers: {}, signal, credentials: 'omit', redirect: 'error', cache: 'no-store', referrerPolicy: 'no-referrer' });
function native(body: ReadableStream<Uint8Array> | null) {
    return { status: 200, headers: { get: () => 'application/json' }, body,
        async arrayBuffer(): Promise<ArrayBuffer> { throw new Error('Expo native fast path must not be used'); } };
}
test('Expo transport reads every streamed byte without the native buffering fast path and retains HTTP policy', async () => {
    const bytes = new TextEncoder().encode('中文😀'); let captured: RelayFetchInit | undefined;
    const fetch = createExpoRelayFetch(async (_, options) => { captured = options; return native(new ReadableStream({ start(controller) { controller.enqueue(bytes.slice(0, 4)); controller.enqueue(bytes.slice(4)); controller.close(); } })); });
    const response = await fetch('https://relay.example', init(new AbortController().signal));
    expect(new Uint8Array(await response.arrayBuffer())).toEqual(bytes);
    await expect(response.arrayBuffer()).rejects.toThrow('already been consumed');
    expect(captured).toMatchObject({ credentials: 'omit', redirect: 'error', headers: { 'Cache-Control': 'no-store, no-cache', Pragma: 'no-cache' } });
});
test('Expo transport cancellation after headers cancels the reader and settles the pending body with the caller reason', async () => {
    const abort = new AbortController(); const reason = new Error('caller stop'); let canceled: unknown;
    const fetch = createExpoRelayFetch(async () => native(new ReadableStream({ cancel(value) { canceled = value; } })));
    const response = await fetch('https://relay.example', init(abort.signal)); const pending = response.arrayBuffer(); abort.abort(reason);
    await expect(pending).rejects.toBe(reason); expect(canceled).toBe(reason);
});
test('Expo transport propagates a truncated body failure instead of hanging or accepting partial bytes', async () => {
    const failure = new Error('connection closed during body'); let source: ReadableStreamDefaultController<Uint8Array> | undefined;
    const fetch = createExpoRelayFetch(async () => native(new ReadableStream({ start(controller) { source = controller; controller.enqueue(new Uint8Array([1])); } })));
    const response = await fetch('https://relay.example', init(new AbortController().signal)); const pending = response.arrayBuffer();
    await Promise.resolve(); source!.error(failure); await expect(pending).rejects.toBe(failure);
});
test('HTTP relay deadlines settle an unfinished Expo response body and retain TimeoutError', async () => {
    let canceled = 0;
    const fetch = createExpoRelayFetch(async () => native(new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode('{"unfinished":')); }, cancel() { canceled++; } })));
    const http = new HttpRelayTransport({ fetch, requestTimeoutMilliseconds: 50 });
    try { await expect(http.request('https://relay.example', 'GET', 'relay.info')).rejects.toMatchObject({ name: 'TimeoutError' }); expect(canceled).toBe(1); }
    finally { http.dispose(); }
});
test('Expo transport rejects an already canceled request before invoking native fetch', async () => {
    const abort = new AbortController(); const reason = new Error('already stopped'); abort.abort(reason); let calls = 0;
    const fetch = createExpoRelayFetch(async () => { calls++; return native(null); });
    await expect(fetch('https://relay.example', init(abort.signal))).rejects.toBe(reason); expect(calls).toBe(0);
});
test('Expo transport handles an absent body and forwards explicit body cancellation', async () => {
    const absent = await createExpoRelayFetch(async () => native(null))('https://relay.example', init(new AbortController().signal));
    expect((await absent.arrayBuffer()).byteLength).toBe(0);
    let canceled = 0; const response = await createExpoRelayFetch(async () => native(new ReadableStream({ cancel() { canceled++; } })))('https://relay.example', init(new AbortController().signal));
    await response.body!.cancel(); expect(canceled).toBe(1); await expect(response.arrayBuffer()).rejects.toThrow('already been consumed');
});
