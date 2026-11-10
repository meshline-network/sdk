import { validateHistoryRows } from '../models/history.js';
import { throwIfAborted } from '../runtime/clock.js';
import type { MeshlineStore, RecordQuery, StoredRecord, StoreReadSnapshot } from '../storage/store.js';

/** Keep one component version across all pages, releasing it even if cancellation occurs between pulls. */
export async function* snapshotRecords<T>(store: MeshlineStore, collections: readonly string[],
    records: (snapshot: StoreReadSnapshot) => AsyncIterable<T>, signal: AbortSignal): AsyncGenerator<T> {
    const snapshot = await store.openSnapshot(collections, signal);
    let disposal: Promise<void> | undefined;
    const dispose = () => disposal ??= snapshot.dispose();
    const abort = () => { void dispose().catch(() => {}); };
    signal.addEventListener('abort', abort, { once: true });
    try { throwIfAborted(signal); yield* records(snapshot); }
    finally { signal.removeEventListener('abort', abort); await dispose(); }
}

/** Forward scans bound decoded records in memory while retaining the caller's fixed snapshot. */
export async function* backupRows(snapshot: StoreReadSnapshot, query: Pick<RecordQuery, 'collection' | 'prefix'>, signal?: AbortSignal): AsyncGenerator<StoredRecord> {
    let after: string | undefined;
    for (;;) {
        throwIfAborted(signal);
        const page = { ...query, limit: 64, ...(after === undefined ? {} : { after }) };
        const rows = (await snapshot.read([page], signal)).sets[0]!;
        validateHistoryRows(rows, page);
        for (const row of rows) { throwIfAborted(signal); yield row; }
        if (rows.length < page.limit) return;
        after = rows.at(-1)!.key;
    }
}

/** Merge ordered scans without materializing their contents. Equal keys are emitted once. */
export async function* mergeBackupRows(snapshot: StoreReadSnapshot, queries: readonly Pick<RecordQuery, 'collection' | 'prefix'>[],
    order: (row: StoredRecord) => string = row => row.key, signal?: AbortSignal): AsyncGenerator<StoredRecord> {
    const iterators = queries.map(query => backupRows(snapshot, query, signal));
    const heads: IteratorResult<StoredRecord>[] = [];
    try {
        for (const iterator of iterators) heads.push(await iterator.next());
        let previous: string | undefined;
        for (;;) {
            let selected = -1; let key: string | undefined;
            heads.forEach((head, index) => { if (!head.done && (key === undefined || order(head.value) < key)) { selected = index; key = order(head.value); } });
            if (selected < 0) return;
            if (key !== previous) { yield heads[selected]!.value!; previous = key; }
            heads[selected] = await iterators[selected]!.next();
        }
    } finally { for (const iterator of iterators) await iterator.return(undefined); }
}
