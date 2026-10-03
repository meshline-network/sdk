import { afterAll, beforeAll, expect, test } from 'vitest';
import { HttpRelayTransport, RpcConnection } from '../../packages/sdk/src/index.js';
import { createNodeRelayFetch, createNodeSocketFactory } from '../../packages/transport-node/src/index.js';
import { startTlsPeer, tlsMaterial, type TlsMaterial } from '../support/tls-peer.js';

let material: TlsMaterial;
let peer: Awaited<ReturnType<typeof startTlsPeer>>;
beforeAll(async () => {
    material = await tlsMaterial();
    peer = await startTlsPeer(material, (request, response) => {
        if (request.url?.startsWith('/redirect/')) { response.writeHead(302, { location: `${peer.https}/redirect-target` }); response.end(); return; }
        if (request.url?.startsWith('/bad-json/')) { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end('{"x":1,"x":2}'); return; }
        response.writeHead(200, { 'Content-Type': 'application/json', 'Set-Cookie': 'test=credential; Secure; HttpOnly; SameSite=None' });
        response.end(JSON.stringify({ method: request.method, url: request.url, cookie: request.headers.cookie ?? null }));
    });
    peer.websocket.on('connection', (socket, request) => {
        if (request.url === '/binary') { socket.send(new Uint8Array([1, 2])); return; }
        if (request.url === '/oversized') { socket.send('x'.repeat(1048577)); return; }
        if (request.url === '/silent') return;
        socket.on('message', data => {
            const input = JSON.parse(String(data)) as { id: string; method: string };
            if (input.method === 'test.disconnect') { socket.terminate(); return; }
            if (input.method === 'auth.device.verify') {
                socket.send(JSON.stringify({ jsonrpc: '2.0', id: input.id, result: { token: 'test', mode: 'device', expires_at: 2000000000 } }));
                socket.send(JSON.stringify({ jsonrpc: '2.0', method: 'message.timeline.changed', params: { after: 3 } }));
            } else socket.send(JSON.stringify({ jsonrpc: '2.0', id: input.id, result: { method: input.method, unicode: '中文😀' } }));
        });
    });
});
afterAll(async () => { await peer?.close(); expect(peer?.failures).toEqual([]); });

test('Node HTTPS validates TLS with only the explicit local CA and sends real protocol requests', async () => {
    const untrusted = new HttpRelayTransport({ fetch: createNodeRelayFetch() });
    await expect(untrusted.request(peer.https, 'GET', 'relay.info')).rejects.toThrow();
    untrusted.dispose();
    const http = new HttpRelayTransport({ fetch: createNodeRelayFetch({ ca: material.ca }) });
    expect(await http.request(peer.https, 'GET', 'relay.info', { query: '中文😀' })).toMatchObject({ method: 'GET', cookie: null });
    expect(await http.request(peer.https, 'PATCH', 'channel.post.edit', { revision: 4 }, 'test-token')).toMatchObject({ method: 'PATCH', cookie: null });
    expect(peer.requests.at(-1)).toMatchObject({ url: '/channel/post/edit', body: '{"revision":4}', headers: { 'x-meshline-session': 'test-token' } });
    await expect(http.request(`${peer.https}/bad-json`, 'GET', 'relay.info')).rejects.toThrow();
    await expect(http.request(`${peer.https}/redirect`, 'POST', 'message.send', {}, 'test-token')).rejects.toThrow('redirect');
    expect(peer.requests.some(request => request.url === '/redirect-target')).toBe(false);
    http.dispose();
});

test('Node WSS validates TLS, exchanges Unicode RPC and accepts post-auth notifications', async () => {
    const rejected = new RpcConnection(peer.wss, { socketFactory: createNodeSocketFactory() });
    await expect(rejected.connect()).rejects.toThrow();
    const socket = new RpcConnection(peer.wss, { socketFactory: createNodeSocketFactory({ ca: material.ca }) });
    expect(await socket.request('relay.info')).toEqual({ method: 'relay.info', unicode: '中文😀' });
    await socket.request('auth.device.verify', {});
    expect(await socket.nextNotification()).toEqual({ method: 'message.timeline.changed', params: { after: 3 } });
    expect(peer.upgrades.at(-1)!.headers.cookie).toBeUndefined();
    expect(peer.upgrades.at(-1)!.headers['sec-websocket-extensions']).toBeUndefined();
    socket.dispose();
});

test('WSS handshake redirects are rejected before any request or credential reaches a target', async () => {
    const socket = new RpcConnection(`${peer.wss}/redirect`, { socketFactory: createNodeSocketFactory({ ca: material.ca }) });
    await expect(socket.request('auth.challenge', { account: 'test' })).rejects.toThrow();
    expect(peer.upgrades.some(request => request.url === '/redirect-target')).toBe(false);
});

test.each([['binary', 1003], ['oversized', 1009]] as const)('Node %s frame rejection sends close %s on the wire', async (path, expectedCode) => {
    const closed = new Promise<number>(resolve => peer.websocket.once('connection', socket => socket.once('close', code => resolve(code))));
    const connection = new RpcConnection(`${peer.wss}/${path}`, { socketFactory: createNodeSocketFactory({ ca: material.ca }) });
    await connection.connect();
    expect((await connection.closed).error).toBeDefined();
    expect(await closed).toBe(expectedCode);
});

test('a connection lost after business send produces an uncertain failure and no replay', async () => {
    const socket = new RpcConnection(peer.wss, { socketFactory: createNodeSocketFactory({ ca: material.ca }) });
    const before = peer.upgrades.length;
    await expect(socket.request('test.disconnect')).rejects.toThrow('closed');
    expect(peer.upgrades.length - before).toBe(1);
});

test('timeout leaves a business request unreplayed and disposal closes pending sockets', async () => {
    const socket = new RpcConnection(`${peer.wss}/silent`, { socketFactory: createNodeSocketFactory({ ca: material.ca }), requestTimeoutMilliseconds: 80 });
    await socket.connect();
    await expect(socket.request('message.send', {})).rejects.toMatchObject({ name: 'TimeoutError' });
    expect(socket.failure).toBeUndefined();
    socket.dispose(); expect((await socket.closed).error).toMatchObject({ name: 'AbortError' });
});
