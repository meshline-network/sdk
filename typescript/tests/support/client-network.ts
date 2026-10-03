import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { systemRandom } from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { PortableClientNetwork } from '../portable/client-network.js';
import { removeTestDirectory } from './temp.js';

export class ClientNetwork extends PortableClientNetwork {
    readonly directories: string[];
    constructor() {
        const directories: string[] = [];
        super({ random: systemRandom, async createStore(path) {
            if (!path) { const directory = await mkdtemp(join(tmpdir(), 'meshline-client-')); directories.push(directory); path = join(directory, 'state.sqlite'); }
            return { store: new NodeSqliteStore(path), path };
        } });
        this.directories = directories;
    }
    override async dispose(): Promise<void> { await super.dispose(); for (const directory of this.directories) await removeTestDirectory(directory); }
}
