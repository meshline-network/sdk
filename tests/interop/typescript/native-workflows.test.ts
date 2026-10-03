import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from 'vitest';
import { systemRandom } from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { createNodeRelayFetch } from '@meshline/transport-node';
import { runInteropChecks, type InteropResult } from '../../../typescript/tests/portable/interop.js';
import { createNativeInteropPeer } from '../../../typescript/tests/support/native-interop-peer.js';
import { startTlsPeer, tlsMaterial } from '../../../typescript/tests/support/tls-peer.js';
import { removeTestDirectory } from '../../../typescript/tests/support/temp.js';

test('portable native acceptance exchanges real HTTPS group workflows with the actual .NET SDK and separate stores', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-native-interop-')); const material = await tlsMaterial();
    let fixture: ReturnType<typeof createNativeInteropPeer>;
    const peer = await startTlsPeer(material, async (request, response) => { if (!await fixture.http(request, response, peer.requests.at(-1)!.body)) { response.statusCode = 404; response.end(); } });
    fixture = createNativeInteropPeer(peer.https, join(directory, 'dotnet'), fileURLToPath(new URL('../dotnet/bin/Release/net10.0/Meshline.Interop.dll', import.meta.url)));
    const results: InteropResult[] = [];
    try {
        await runInteropChecks({ origin: peer.https, fetch: createNodeRelayFetch({ ca: material.ca }), random: systemRandom, createStore: name => new NodeSqliteStore(join(directory, name)) }, result => { results.push(result); });
        expect(results).toHaveLength(27); expect(results.filter(value => !value.passed)).toEqual([]); expect(peer.failures).toEqual([]);
        const snapshot = Object.values(fixture.snapshots())[0]!; expect(snapshot.closed).toBe(true); expect(snapshot.errors).toEqual([]);
        expect(snapshot.requests.some(value => value.actor === 'typescript' && value.method === 'group.application.approve')).toBe(true);
        expect(snapshot.requests.some(value => value.actor === 'dotnet' && value.method === 'group.application.approve')).toBe(true);
    } catch (cause) { throw new Error(`Native interop failed after ${results.filter(value => value.passed).length} checks: ${JSON.stringify(Object.values(fixture.snapshots()).map(value => value.errors))}`, { cause }); }
    finally { await fixture.dispose(); await peer.close(); await removeTestDirectory(directory); }
}, 240000);
