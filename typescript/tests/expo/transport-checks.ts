import { HttpRelayTransport, RpcConnection, requireObject, decodeUtf8, type RelaySocket } from '@meshline/sdk';
import { createExpoSocketFactory, expoRandom, expoRelayFetch } from '@meshline/expo';
import type { VectorResult } from './portable-vectors';

const origin = 'https://127.0.0.1:18443';
const wss = 'wss://127.0.0.1:18443';
const delay = (milliseconds: number) => new Promise<void>(resolve => setTimeout(resolve, milliseconds));
function check(condition: unknown, message: string): asserts condition { if (!condition) throw new Error(message); }
async function rejects(action: () => Promise<unknown>): Promise<void> { let rejected = false; try { await action(); } catch { rejected = true; } check(rejected, 'The operation unexpectedly succeeded.'); }
async function bounded<T>(operation: Promise<T>, milliseconds = 8000): Promise<T> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try { return await Promise.race([operation, new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('Native check deadline exceeded.')), milliseconds); })]); }
    finally { clearTimeout(timer); }
}
interface Observations {
    requests: { trusted: boolean; url: string; headers: Record<string, string>; body: string }[];
    upgrades: { trusted: boolean; url: string; headers: Record<string, string> }[];
    closes: { url: string; code?: number; disconnected?: boolean }[];
    messages: { url: string; method: string }[];
    errors: string[];
}
async function control(path: string): Promise<Observations> {
    const abort = new AbortController(); const timer = setTimeout(() => abort.abort(), 5000);
    try {
        const response = await expoRelayFetch(`${origin}/${path}`, { method: 'GET', headers: {}, signal: abort.signal,
            credentials: 'omit', redirect: 'error', cache: 'no-store', referrerPolicy: 'no-referrer' });
        check(response.status === 200, 'Fixture control request failed.'); return JSON.parse(decodeUtf8(new Uint8Array(await response.arrayBuffer()))) as Observations;
    } finally { clearTimeout(timer); }
}
async function observed(predicate: (value: Observations) => boolean): Promise<Observations> {
    const deadline = Date.now() + 6000;
    do { const value = await control('observations'); if (predicate(value)) return value; await delay(100); } while (Date.now() < deadline);
    throw new Error('Required server-side observation was absent.');
}

/** Only talks to the separately started local TLS fixture; never a deployed relay. */
export async function runTransportChecks(onResult: (result: VectorResult) => void): Promise<readonly VectorResult[]> {
    const results: VectorResult[] = [];
    async function run(name: string, action: () => Promise<void>): Promise<void> {
        let result: VectorResult; try { await action(); result = { name, passed: true }; }
        catch (error) { result = { name, passed: false, error: String(error) }; }
        results.push(result); onResult(result); await delay(0);
    }
    const factory = createExpoSocketFactory();
    const connection = (path: string, endpoint = wss) => new RpcConnection(endpoint + path, { socketFactory: factory, random: expoRandom, requestTimeoutMilliseconds: 5000 });
    await run('TLS fixture connection and server observation reset', async () => { await control('reset'); });
    await run('HTTPS Unicode request and response', async () => {
        const http = new HttpRelayTransport({ fetch: expoRelayFetch, requestTimeoutMilliseconds: 5000 });
        try {
            const response = requireObject((await http.request(origin, 'POST', 'relay.info', { text: '中文😀' }))!);
            check(response.unicode === '中文😀' && requireObject(response.body!).text === '中文😀', 'Unicode round trip differs.');
        } finally { http.dispose(); }
    });
    await run('HTTPS preserves every immediate response under concurrent native dispatch', async () => {
        const http = new HttpRelayTransport({ fetch: expoRelayFetch, requestTimeoutMilliseconds: 5000 });
        let next = 0;
        try {
            await Promise.all(Array.from({ length: 8 }, async () => {
                for (;;) {
                    const id = next++; if (id >= 1024) return;
                    const response = requireObject((await http.request(`${origin}/echo/start-${id}`, 'GET', 'relay.info'))!);
                    check(response.unicode === '中文😀' && response.cookie === null && response.body === null, `Incomplete immediate response ${id}`);
                }
            }));
        } finally { http.dispose(); }
    });
    await run('HTTPS rejects a certificate signed by an untrusted CA', async () => {
        const http = new HttpRelayTransport({ fetch: expoRelayFetch, requestTimeoutMilliseconds: 5000 });
        try { await rejects(() => http.request('https://127.0.0.1:18444', 'GET', 'relay.info')); }
        finally { http.dispose(); }
        check(!(await control('observations')).requests.some(row => !row.trusted), 'Untrusted server received an HTTP request.');
    });
    await run('HTTPS omits ambient cookies and ignores Set-Cookie', async () => {
        // Establish that the application has an ambient cookie before testing omission.
        await globalThis.fetch(`${origin}/seed`, { credentials: 'include' });
        const before = await (await globalThis.fetch(`${origin}/ambient`, { credentials: 'include' })).json() as { cookie: string | null };
        check(before.cookie?.includes('ambient=meshline-test'), 'Ambient cookie seed was not observable.');
        const http = new HttpRelayTransport({ fetch: expoRelayFetch, requestTimeoutMilliseconds: 5000 });
        try {
            for (let i = 0; i < 2; i++) check(requireObject((await http.request(`${origin}/echo`, 'GET', 'relay.info'))!).cookie === null, 'SDK sent an ambient cookie.');
        } finally { http.dispose(); }
        const after = await (await globalThis.fetch(`${origin}/ambient`, { credentials: 'include' })).json() as { cookie: string | null };
        check(!after.cookie?.includes('injected='), 'SDK accepted a Set-Cookie response.');
        const observations = await control('observations');
        check(observations.requests.filter(row => row.url.startsWith('/echo/')).every(row => row.headers.cookie === undefined), 'Server observed an SDK cookie.');
    });
    await run('HTTPS rejects redirects before forwarding session tokens', async () => {
        const http = new HttpRelayTransport({ fetch: expoRelayFetch, requestTimeoutMilliseconds: 5000 });
        try { await rejects(() => http.request(`${origin}/redirect`, 'POST', 'message.send', {}, 'native-test-token')); }
        finally { http.dispose(); }
        const observations = await control('observations');
        check(observations.requests.some(row => row.url === '/redirect/message/send' && row.headers['x-meshline-session'] === 'native-test-token'), 'Initial authenticated request was absent.');
        check(!observations.requests.some(row => row.url === '/redirect-target'), 'Redirect target was reached.');
    });
    for (const mode of ['cancel', 'reset'] as const) await run(`HTTPS ${mode} after headers rejects the pending body and closes its request`, async () => {
        const abort = new AbortController(); const path = mode === 'cancel' ? '/body-delay/cancel' : '/body-reset';
        try {
            const response = await expoRelayFetch(origin + path, { method: 'GET', headers: {}, signal: abort.signal, credentials: 'omit', redirect: 'error', cache: 'no-store', referrerPolicy: 'no-referrer' });
            check(response.status === 200, 'Body fixture headers were not received');
            const pending = response.arrayBuffer().then(bytes => ({ rejected: false, name: '', bytes: bytes.byteLength }), error => ({ rejected: true, name: error instanceof Error ? error.name : String(error), error: String(error) }));
            if (mode === 'cancel') { await delay(100); abort.abort(); }
            const result = await bounded(pending, 2500); check(result.rejected && (mode !== 'cancel' || result.name === 'AbortError'), `Incomplete or canceled body did not reject with its original failure: ${JSON.stringify(result)}`);
            await observed(value => value.closes.some(row => row.url === path && row.disconnected));
        } finally { abort.abort(); }
    });
    await run('HTTPS deadline cancels an unfinished response body with TimeoutError', async () => {
        const http = new HttpRelayTransport({ fetch: expoRelayFetch, requestTimeoutMilliseconds: 1000 });
        try {
            const pending = http.request(`${origin}/body-delay`, 'GET', 'relay.info').then(() => '', error => error instanceof Error ? error.name : String(error));
            const result = await bounded(pending, 2500); check(result === 'TimeoutError', `Response body deadline did not retain TimeoutError: ${result}`);
            await observed(value => value.closes.some(row => row.url === '/body-delay/relay/info' && row.disconnected));
        } finally { http.dispose(); }
    });
    await run('WSS Unicode RPC and ambient-cookie isolation', async () => {
        const socket = connection('/rpc');
        try { check(requireObject(await socket.request('relay.info')).unicode === '中文😀', 'Unicode RPC differs.'); }
        finally { socket.dispose(); }
        const observed = (await control('observations')).upgrades.filter(row => row.url === '/rpc');
        check(observed.length === 1 && observed[0]!.headers.cookie === undefined, 'Missing handshake or ambient cookie was sent.');
    });
    await run('WSS rejects a certificate signed by an untrusted CA', async () => {
        const socket = connection('/rpc', 'wss://127.0.0.1:18444');
        try { await rejects(() => socket.connect()); } finally { socket.dispose(); }
        check(!(await control('observations')).upgrades.some(row => !row.trusted), 'Untrusted server received a WebSocket handshake.');
    });
    await run('WSS rejects handshake redirects', async () => {
        const socket = connection('/redirect'); try { await rejects(() => socket.connect()); } finally { socket.dispose(); }
        const observations = await control('observations');
        check(observations.upgrades.some(row => row.url === '/redirect'), 'Redirect fixture was not reached.');
        check(!observations.upgrades.some(row => row.url === '/redirect-target'), 'WebSocket redirect target was reached.');
    });
    for (const [path, code] of [['binary', 1003], ['oversized', 1009]] as const) await run(`WSS ${path} rejection sends close ${code}`, async () => {
        const socket = connection(`/${path}`);
        try { await socket.connect(); check((await bounded(socket.closed)).error, 'Invalid frame was not rejected.'); }
        finally { socket.dispose(); }
        await observed(value => value.closes.some(row => row.url === `/${path}` && row.code === code));
    });
    await run('WSS cancellation during handshake', async () => {
        const socket = connection('/opening'); const abort = new AbortController();
        const rejected = rejects(() => socket.connect(abort.signal));
        try { await observed(value => value.upgrades.some(row => row.url === '/opening')); abort.abort(); await bounded(rejected); }
        finally { abort.abort(); socket.dispose(); }
    });
    await run('WSS cancellation of a pending request does not replay it', async () => {
        const socket = connection('/silent'); const abort = new AbortController();
        try {
            await socket.connect(); const pending = rejects(() => socket.request('message.send', {}, abort.signal)); await delay(100); abort.abort(); await bounded(pending);
            const observations = await control('observations');
            check(observations.upgrades.filter(row => row.url === '/silent').length === 1, 'Cancellation opened a replacement connection.');
            check(observations.messages.filter(row => row.url === '/silent' && row.method === 'message.send').length === 1, 'Pending business request was missing or replayed.');
        } finally { socket.dispose(); }
    });
    await run('native socket close cleanup is bounded for an unresponsive peer', async () => {
        const socket: RelaySocket = factory(`${wss}/unresponsive-close`);
        try {
            await bounded(new Promise<void>((resolve, reject) => { socket.on('open', () => resolve()); socket.on('error', reject); }));
            const closed = new Promise<void>(resolve => socket.on('close', () => resolve()));
            const started = Date.now(); socket.close(1000, 'native-test'); await bounded(closed, 6500);
            check(socket.readyState === 3 && Date.now() - started < 6500, 'Native close did not finish within its deadline.');
            await observed(value => value.closes.some(row => row.url === '/unresponsive-close' && row.disconnected));
        } finally { if (socket.readyState < 2) socket.close(1000, 'cleanup'); }
    });
    await run('TLS peer recorded no fixture errors', async () => { check((await control('observations')).errors.length === 0, 'Server fixture recorded errors.'); });
    return results;
}
