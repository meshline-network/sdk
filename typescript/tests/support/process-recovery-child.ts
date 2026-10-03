import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { NodeSqliteStore } from '@meshline/storage-node';
import { createNodeRelayFetch } from '@meshline/transport-node';
import { systemRandom } from '@meshline/sdk';
import { prepareProcessRecovery, verifyProcessRecovery } from '../portable/process-recovery.js';
import type { InteropResult } from '../portable/interop.js';

const config = JSON.parse(await readFile(process.argv[2]!, 'utf8')) as { origin: string; ca: string; directory: string; phase: 'prepare' | 'verify' };
const options = { origin: config.origin, fetch: createNodeRelayFetch({ ca: config.ca }), random: systemRandom, createStore: (name: string) => new NodeSqliteStore(join(config.directory, name)) };
const results: InteropResult[] = []; const record = (result: InteropResult) => { results.push(result); };
if (config.phase === 'prepare') {
    const ready = await prepareProcessRecovery(options, record);
    await writeFile(join(config.directory, 'prepared.json'), JSON.stringify({ state: ready.state, snapshot: ready.snapshot, results }));
    console.log('PROCESS-READY');
    // Intentionally no signal handlers/disposal: the regression terminates this
    // process while HTTP and native SQLite are still open.
    process.stdin.resume();
} else {
    const { state } = JSON.parse(await readFile(join(config.directory, 'prepared.json'), 'utf8'));
    await verifyProcessRecovery(options, state, record, snapshot => writeFile(join(config.directory, 'evidence.json'), JSON.stringify(snapshot)));
    await writeFile(join(config.directory, 'verified.json'), JSON.stringify(results));
}
