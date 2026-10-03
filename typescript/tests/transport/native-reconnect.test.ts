import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
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
    } finally { await peer.close(); await removeTestDirectory(directory); }
}, 90000);
