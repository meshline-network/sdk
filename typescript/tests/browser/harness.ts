import { IndexedDbStore } from '../../packages/storage-browser/src/index.js';
import * as sdk from '@meshline/sdk';
import type { QueryReader, StoredRecord } from '@meshline/sdk';
import { managerRoundtrip } from './managers.js';

const binding = { context: 'neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70', accountId: 'neo:860833102:NgaxELHoZFpQWNwd74Fvq4wF3qz57WTfpp' };
const stores: Record<string, IndexedDbStore> = {};
const readers: Record<string, QueryReader<StoredRecord>> = {};

const harness = {
    sdk, binding, stores, readers, managerRoundtrip,
    async open(name: string, slot = 'main', migrate = true): Promise<void> {
        const store = new IndexedDbStore(name);
        stores[slot] = store;
        if (migrate) await store.migrate();
        await store.initialize(binding);
    },
    async count(name: string, collection: string): Promise<number> {
        const database = await new Promise<IDBDatabase>((resolve, reject) => {
            const operation = indexedDB.open(name);
            operation.onsuccess = () => resolve(operation.result);
            operation.onerror = () => reject(operation.error);
        });
        try {
            return await new Promise<number>((resolve, reject) => {
                const operation = database.transaction(collection, 'readonly').objectStore(collection).count();
                operation.onsuccess = () => resolve(operation.result);
                operation.onerror = () => reject(operation.error);
            });
        } finally { database.close(); }
    },
};

declare global { interface Window { meshlineHarness: typeof harness } }
window.meshlineHarness = harness;
