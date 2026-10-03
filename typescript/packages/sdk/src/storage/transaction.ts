import { StateConflictError } from '../errors.js';
import { throwIfAborted } from '../runtime/clock.js';
import type { MeshlineStore, RecordQuery, StoreMutation, StoreSnapshot } from './store.js';

/** Recomputes local changes on CAS contention. The planner must be synchronous and side-effect free. */
export async function updateStore<T>(store: MeshlineStore, queries: readonly RecordQuery[],
    plan: (snapshot: StoreSnapshot) => { readonly mutations: readonly StoreMutation[]; readonly result: T }, signal?: AbortSignal): Promise<T> {
    for (let attempt = 0; attempt < 16; attempt++) {
        throwIfAborted(signal);
        const snapshot = await store.read(queries, signal);
        const update = plan(snapshot);
        if (!update.mutations.length) return update.result;
        try { await store.commit(snapshot.version, update.mutations, signal); return update.result; }
        catch (error) { if (!(error instanceof StateConflictError) || attempt === 15) throw error; }
    }
    throw new StateConflictError('Storage remained busy.');
}
