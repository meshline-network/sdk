import { mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';
import { MeshlineClient, RelayClientPool } from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { createNodeRelayFetch, createNodeSocketFactory } from '@meshline/transport-node';
import { ownedSession, type ApplicationDependencies } from './shared.js';

export async function migrateAndOpenNodeClient(path: string, application: ApplicationDependencies) {
    await mkdir(dirname(path), { recursive: true });
    const store = new NodeSqliteStore(path);
    const pool = new RelayClientPool({ context: application.context, accountId: application.accountId,
        fetch: createNodeRelayFetch(), socketFactory: createNodeSocketFactory() }, application.registry);
    const session = ownedSession(new MeshlineClient({ ...application, store, relayClients: pool }), pool, store);
    try { await store.migrate(); await session.client.initialize(); return session; }
    catch (error) { try { await session.dispose(); } catch (cleanup) { throw new AggregateError([error, cleanup], 'Client setup and cleanup failed.'); } throw error; }
}
