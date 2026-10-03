import { MeshlineClient, RelayClientPool, browserSocketFactory } from '@meshline/sdk';
import { IndexedDbStore } from '@meshline/storage-browser';
import { ownedSession, type ApplicationDependencies } from './shared.js';

export async function migrateAndOpenBrowserClient(databaseName: string, application: ApplicationDependencies) {
    const store = new IndexedDbStore(databaseName);
    const pool = new RelayClientPool({ context: application.context, accountId: application.accountId, socketFactory: browserSocketFactory }, application.registry);
    const session = ownedSession(new MeshlineClient({ ...application, store, relayClients: pool }), pool, store);
    try { await store.migrate(); await session.client.initialize(); return session; }
    catch (error) { try { await session.dispose(); } catch (cleanup) { throw new AggregateError([error, cleanup], 'Client setup and cleanup failed.'); } throw error; }
}
