import { test, expect } from '@playwright/test';
import { startTlsPeer, tlsMaterial } from '../support/tls-peer.js';
import type {} from './harness.js';

// Test-only localhost certificate; production adapters retain the platform TLS checks.
test.use({ ignoreHTTPSErrors: true });
let peer: Awaited<ReturnType<typeof startTlsPeer>>;
test.beforeAll(async () => {
    peer = await startTlsPeer(await tlsMaterial(), (request, response) => {
        if (request.url?.startsWith('/redirect/')) { response.writeHead(302, { location: `${peer.https}/redirect-target` }); response.end(); return; }
        if (request.url?.startsWith('/invalid/')) { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end('{"a":1,"a":2}'); return; }
        response.writeHead(200, { 'Content-Type': 'application/json', 'Set-Cookie': 'response=secret; Secure; SameSite=None' });
        response.end(JSON.stringify({ method: request.method, url: request.url, cookie: request.headers.cookie ?? null, token: request.headers['x-meshline-session'] ?? null }));
    });
    peer.websocket.on('connection', (socket, request) => {
        if (request.url === '/binary') { socket.send(new Uint8Array([1])); return; }
        if (request.url === '/large') { socket.send('x'.repeat(1048577)); return; }
        socket.on('message', data => {
            const message = JSON.parse(String(data)) as { id: string; method: string };
            if (message.method === 'auth.device.verify') {
                socket.send(JSON.stringify({ jsonrpc: '2.0', id: message.id, result: { token: 'test-token', mode: 'device', expires_at: 2000000000 } }));
                socket.send('{"jsonrpc":"2.0","method":"message.timeline.changed","params":{"after":9}}');
            } else socket.send(JSON.stringify({ jsonrpc: '2.0', id: message.id, result: { text: '中文😀' } }));
        });
    });
});
test.afterAll(async () => { await peer?.close(); expect(peer?.failures).toEqual([]); });
test.beforeEach(async ({ page }) => { await page.goto('/tests/browser/'); await page.waitForFunction(() => Boolean(window.meshlineHarness)); });

test('browser HTTPS sends protocol requests while omitting ambient cookies and ignoring Set-Cookie', async ({ page, context }) => {
    await context.addCookies([{ name: 'ambient', value: 'secret', url: peer.https, secure: true, sameSite: 'None' }]);
    const result = await page.evaluate(async endpoint => {
        const http = new window.meshlineHarness.sdk.HttpRelayTransport();
        try {
            const first = await http.request(endpoint, 'GET', 'relay.info', { q: '中文😀' });
            const second = await http.request(endpoint, 'PATCH', 'channel.post.edit', { revision: 1 }, 'test-token');
            return { first, second };
        } finally { http.dispose(); }
    }, peer.https);
    expect(result.first).toMatchObject({ method: 'GET', cookie: null, token: null });
    expect(result.second).toMatchObject({ method: 'PATCH', cookie: null, token: 'test-token' });
    expect((await context.cookies(peer.https)).some(cookie => cookie.name === 'response')).toBe(false);
});

test('browser rejects HTTP and WebSocket redirects and malformed JSON', async ({ page }) => {
    const result = await page.evaluate(async endpoints => {
        const { HttpRelayTransport, RpcConnection } = window.meshlineHarness.sdk;
        const http = new HttpRelayTransport();
        const failures: string[] = [];
        for (const endpoint of [`${endpoints.https}/redirect`, `${endpoints.https}/invalid`]) {
            try { await http.request(endpoint, 'POST', 'message.send', {}, 'test-token'); failures.push('unexpected success'); }
            catch (error) { failures.push((error as Error).name); }
        }
        const socket = new RpcConnection(`${endpoints.wss}/redirect`);
        try { await socket.request('auth.challenge', {}); failures.push('unexpected success'); }
        catch (error) { failures.push((error as Error).name); }
        finally { socket.dispose(); http.dispose(); }
        return failures;
    }, { https: peer.https, wss: peer.wss });
    expect(result).toHaveLength(3); expect(result).not.toContain('unexpected success');
    expect(peer.requests.some(request => request.url === '/redirect-target')).toBe(false);
    expect(peer.upgrades.some(request => request.url === '/redirect-target')).toBe(false);
});

test('browser WSS exchanges Unicode RPC and retains notifications immediately after authentication', async ({ page }) => {
    const result = await page.evaluate(async endpoint => {
        const socket = new window.meshlineHarness.sdk.RpcConnection(endpoint);
        try {
            const response = await socket.request('relay.info');
            await socket.request('auth.device.verify', {});
            const notification = await socket.nextNotification();
            return { response, notification };
        } finally { socket.dispose(); }
    }, peer.wss);
    expect(result).toEqual({ response: { text: '中文😀' }, notification: { method: 'message.timeline.changed', params: { after: 9 } } });
});

test('browser closes forbidden frames and reports its constrained close codes explicitly', async ({ page }) => {
    const result = await page.evaluate(async endpoint => {
        const failures = [];
        for (const path of ['binary', 'large']) {
            const socket = new window.meshlineHarness.sdk.RpcConnection(`${endpoint}/${path}`);
            await socket.connect();
            const failure = await socket.closed;
            failures.push({ requested: failure.requestedCloseCode, actual: failure.sentCloseCode });
        }
        return failures;
    }, peer.wss);
    expect(result).toEqual([{ requested: 1003, actual: 3003 }, { requested: 1009, actual: 3009 }]);
});
