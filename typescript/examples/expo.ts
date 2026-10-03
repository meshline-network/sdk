import { MeshlineClient, RelayClientPool } from '@meshline/sdk';
import { ExpoSqliteStore, expoRandom, expoRelayFetch, createExpoSocketFactory } from '@meshline/expo';
import { ownedSession, type ApplicationDependencies } from './shared.js';

/** Requires a native build containing MeshlineRelaySocket; Expo Go does not contain it. */
export async function migrateAndOpenExpoClient(databaseName: string, application: ApplicationDependencies) {
    const socketFactory = createExpoSocketFactory();
    const store = new ExpoSqliteStore({ databaseName });
    const pool = new RelayClientPool({ context: application.context, accountId: application.accountId, fetch: expoRelayFetch, random: expoRandom, socketFactory }, application.registry);
    const session = ownedSession(new MeshlineClient({ ...application, store, relayClients: pool, random: expoRandom }), pool, store);
    try { await store.migrate(); await session.client.initialize(); return session; }
    catch (error) { try { await session.dispose(); } catch (cleanup) { throw new AggregateError([error, cleanup], 'Client setup and cleanup failed.'); } throw error; }
}
