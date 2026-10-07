import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Socket } from 'node:net';
import { afterEach, expect, test, vi } from 'vitest';
import { AbortController as LegacyAbortController } from 'abort-controller';
import { systemRandom } from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { createNodeRelayFetch, createNodeSocketFactory } from '@meshline/transport-node';
import { runReconnectChecks, type ReconnectResult } from '../portable/reconnect.js';
import { createReconnectPeer } from '../support/reconnect-peer.js';
import { startTlsPeer, tlsMaterial } from '../support/tls-peer.js';
import { removeTestDirectory } from '../support/temp.js';

afterEach(() => vi.unstubAllGlobals());

test('reconnect control requests survive an idle connection closing without disabling business connection reuse', async () => {
    const material = await tlsMaterial();
    const sockets: Socket[] = [];
    let fixture: ReturnType<typeof createReconnectPeer>;
    const peer = await startTlsPeer(material, (request, response) => {
        sockets.push(request.socket);
        if (!fixture.http(request, response, peer.requests.at(-1)!.body)) { response.statusCode = 404; response.end('{}'); }
    });
    fixture = createReconnectPeer(peer.https, peer.wss);
    const fetch = createNodeRelayFetch({ ca: material.ca });
    const run = 'a'.repeat(24);
    async function query(path: string, body?: object) {
        const response = await fetch(`${peer.https}/reconnect/${path}`, {
            method: body ? 'POST' : 'GET', headers: body ? { 'Content-Type': 'application/json' } : {},
            ...(body ? { body: JSON.stringify(body) } : {}), signal: new AbortController().signal,
            credentials: 'omit', redirect: 'error', cache: 'no-store', referrerPolicy: 'no-referrer',
        });
        expect(response.status).toBe(200);
        return JSON.parse(new TextDecoder().decode(await response.arrayBuffer()));
    }
    try {
        await query('begin', { run });
        const observing = query(`${run}/observations`);
        // Reproduce the idle-close race before the peer can handle the next request.
        sockets[0]!.destroy();
        expect((await observing).errors).toEqual([]);
        expect(sockets[1]).not.toBe(sockets[0]);
        await query(`${run}/v1/relay/info`);
        await query(`${run}/v1/relay/info`);
        expect(sockets[3]).toBe(sockets[2]);
        expect(peer.requests).toHaveLength(4);
        expect(peer.failures).toEqual([]);
    } finally { await peer.close(); }
});

test.each(['platform', 'React Native legacy'])('real TLS reconnect and subscription cleanup preserve cancellation with %s controllers', async mode => {
    if (mode === 'React Native legacy') vi.stubGlobal('AbortController', LegacyAbortController);
    const material = await tlsMaterial(); const directory = await mkdtemp(join(tmpdir(), 'meshline-reconnect-'));
    let fixture: ReturnType<typeof createReconnectPeer>;
    const peer = await startTlsPeer(material, (request, response) => {
        if (!fixture.http(request, response, peer.requests.at(-1)!.body)) { response.statusCode = 404; response.end('{}'); }
    });
    fixture = createReconnectPeer(peer.https, peer.wss);
    peer.websocket.on('connection', (socket, request) => { if (!fixture.connect(socket, request)) socket.close(1008); });
    const results: ReconnectResult[] = [];
    try {
        await runReconnectChecks({ origin: peer.https, fetch: createNodeRelayFetch({ ca: material.ca }), socketFactory: createNodeSocketFactory({ ca: material.ca }),
            random: systemRandom, createStore: () => new NodeSqliteStore(join(directory, 'state.sqlite')) }, result => { results.push(result); });
        expect(results).toHaveLength(14); expect(results.filter(result => !result.passed)).toEqual([]); expect(peer.failures).toEqual([]);
    } catch (cause) {
        const completed = results.filter(result => result.passed).map(result => result.name);
        throw new Error(`Native reconnect failed after: ${completed.join('; ') || 'no completed checks'}`, { cause });
    } finally { await peer.close(); await removeTestDirectory(directory); }
}, 90000);
