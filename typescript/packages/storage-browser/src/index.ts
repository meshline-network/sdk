import {
    AsyncGate, StateConflictError, bindingJson, canonicalJson, encodeBase64Url, parseJson,
    prepareCommit, requireBatchCount, requireObject, throwIfAborted, systemRandom, validateRecordQuery,
    type MeshlineStore, type QueryReader, type RandomSource, type RecordQuery, type StorageBinding,
    type StoredRecord, type StoreMutation, type StoreSnapshot,
} from '@meshline/sdk';

interface DiskRecord { collection: string; key: string; revision: number; value: string }
interface SnapshotRow { owner: string; reader: string; index: number; record: DiskRecord }
interface Owner { id: string }

/** Explicit platform dependencies permit worker use without importing DOM application code. */
export interface IndexedDbOptions {
    readonly indexedDB?: IDBFactory;
    readonly locks?: LockManager;
    readonly random?: RandomSource;
}

function request<T>(operation: IDBRequest<T>): Promise<T> {
    return new Promise((resolve, reject) => {
        operation.onsuccess = () => resolve(operation.result);
        operation.onerror = () => reject(operation.error ?? new Error('IndexedDB request failed.'));
    });
}

async function transaction<T>(database: IDBDatabase, stores: string[], mode: IDBTransactionMode, action: (value: IDBTransaction) => Promise<T>, signal?: AbortSignal): Promise<T> {
    throwIfAborted(signal);
    const current = database.transaction(stores, mode, mode === 'readwrite' ? { durability: 'strict' } : {});
    const abort = (): void => { current.abort(); };
    signal?.addEventListener('abort', abort, { once: true });
    const completed = new Promise<void>((resolve, reject) => {
        current.oncomplete = () => resolve();
        current.onabort = () => reject(signal?.aborted ? signal.reason : current.error ?? new Error('IndexedDB transaction aborted.'));
        current.onerror = () => { /* onabort supplies the final transaction outcome. */ };
    });
    let settled = false;
    current.addEventListener('complete', () => { settled = true; });
    current.addEventListener('abort', () => { settled = true; });
    const work = action(current).catch(error => {
        if (!settled) {
            try { current.abort(); }
            catch (abortError) {
                if (!(abortError instanceof DOMException && abortError.name === 'InvalidStateError')) throw new AggregateError([error, abortError], 'IndexedDB rollback failed.');
            }
        }
        throw error;
    });
    try {
        const [result] = await Promise.all([work, completed]);
        return result;
    } catch (error) {
        await Promise.allSettled([work, completed]);
        throw error;
    } finally { signal?.removeEventListener('abort', abort); }
}

function keyRange(query: RecordQuery): IDBKeyRange {
    validateRecordQuery(query);
    if (query.key !== undefined) return IDBKeyRange.only([query.collection, query.key]);
    const prefix = query.prefix ?? '';
    return IDBKeyRange.bound([query.collection, prefix], [query.collection, prefix + '\uffff']);
}

function decode(record: DiskRecord): StoredRecord {
    return { collection: record.collection, key: record.key, revision: record.revision, value: requireObject(parseJson(record.value)) };
}

function scan(store: IDBObjectStore, range: IDBKeyRange, reverse: boolean, visit: (cursor: IDBCursorWithValue) => boolean | void): Promise<void> {
    return new Promise((resolve, reject) => {
        const operation = store.openCursor(range, reverse ? 'prev' : 'next');
        operation.onerror = () => reject(operation.error);
        operation.onsuccess = () => {
            const cursor = operation.result;
            if (!cursor) { resolve(); return; }
            try {
                if (visit(cursor) === false) resolve();
                else cursor.continue();
            } catch (error) { reject(error); }
        };
    });
}

/** IndexedDB storage with atomic commits, cross-tab conflicts and durable snapshots. */
export class IndexedDbStore implements MeshlineStore {
    private readonly gate = new AsyncGate();
    private database: IDBDatabase | undefined;
    private owner: string | undefined;
    private releaseOwner: (() => void) | undefined;
    private ownerLock: Promise<void> | undefined;
    private disposed = false;
    private invalidated = false;
    private readonly readers = new Set<IndexedDbReader>();

    constructor(readonly name: string, private readonly options: IndexedDbOptions = {}) {
        if (!name.trim()) throw new TypeError('An IndexedDB database name is required.');
    }

    migrate(signal?: AbortSignal): Promise<void> {
        return this.gate.run(async () => {
            this.assertOpen();
            if (this.database) throw new Error('Migrate before initializing the store.');
            const database = await this.openDatabase(true, signal);
            database.close();
        }, signal);
    }

    initialize(binding: StorageBinding, signal?: AbortSignal): Promise<void> {
        return this.gate.run(async () => {
            this.assertOpen();
            const encoded = bindingJson(binding);
            if (this.database) {
                const actual = await transaction(this.database, ['meta'], 'readonly', async tx => (await request(tx.objectStore('meta').get('binding')) as { value: string }).value, signal);
                if (actual !== encoded) throw new Error('Database is bound to another network or account.');
                return;
            }
            const locks = this.options.locks ?? globalThis.navigator?.locks;
            if (!locks) throw new Error('Web Locks are required to manage snapshot ownership.');
            const database = await this.openDatabase(false, signal);
            const owner = encodeBase64Url((this.options.random ?? systemRandom).bytes(16));
            let release!: () => void;
            const lifetime = new Promise<void>(resolve => { release = resolve; });
            let acquired!: () => void;
            let failed!: (reason: unknown) => void;
            const ready = new Promise<void>((resolve, reject) => { acquired = resolve; failed = reject; });
            const ownerLock = locks.request(this.lockName(owner), async () => { acquired(); await lifetime; });
            ownerLock.catch(failed);
            try {
                await ready;
                await transaction(database, ['meta', 'owners'], 'readwrite', async tx => {
                    const meta = tx.objectStore('meta');
                    const existing = await request(meta.get('binding')) as { value: string } | undefined;
                    if (existing && existing.value !== encoded) throw new Error('Database is bound to another network or account.');
                    if (!existing) await request(meta.put({ key: 'binding', value: encoded }));
                    await request(tx.objectStore('owners').add({ id: owner }));
                }, signal);
                // Only a released owner lock permits cleanup; a suspended live tab keeps its snapshots.
                const owners = await transaction(database, ['owners'], 'readonly', async tx => await request(tx.objectStore('owners').getAll()) as Owner[], signal);
                for (const previous of owners) {
                    if (previous.id === owner) continue;
                    await locks.request(this.lockName(previous.id), { ifAvailable: true }, async lock => {
                        if (lock) await this.removeOwner(database, previous.id, signal);
                    });
                }
                throwIfAborted(signal);
                this.database = database;
                this.owner = owner;
                this.releaseOwner = release;
                this.ownerLock = ownerLock;
                database.onversionchange = () => { this.invalidated = true; database.close(); };
            } catch (error) {
                database.close();
                release();
                await ownerLock;
                throw error;
            }
        }, signal);
    }

    read(queries: readonly RecordQuery[], signal?: AbortSignal): Promise<StoreSnapshot> {
        const selections = queries.map(query => ({ range: keyRange(query), reverse: query.reverse ?? false }));
        return this.gate.run(() => transaction(this.connection(), ['meta', 'records'], 'readonly', async tx => {
            const version = (await request(tx.objectStore('meta').get('version')) as { value: number }).value;
            const sets: StoredRecord[][] = [];
            for (const query of selections) {
                const values: StoredRecord[] = [];
                await scan(tx.objectStore('records'), query.range, query.reverse, cursor => { values.push(decode(cursor.value as DiskRecord)); });
                sets.push(values);
            }
            return { version, sets };
        }, signal), signal);
    }

    commit(expectedVersion: number, mutations: readonly StoreMutation[], signal?: AbortSignal): Promise<number> {
        const changes = prepareCommit(expectedVersion, mutations);
        return this.gate.run(() => transaction(this.connection(), ['meta', 'records'], 'readwrite', async tx => {
            const meta = tx.objectStore('meta');
            const version = (await request(meta.get('version')) as { value: number }).value;
            if (version !== expectedVersion) throw new StateConflictError('Database revision changed before commit.');
            const next = version + 1;
            for (const change of changes) {
                if (change.kind === 'put') await request(tx.objectStore('records').put({ collection: change.collection, key: change.key, revision: next, value: canonicalJson(change.value) }));
                else await request(tx.objectStore('records').delete([change.collection, change.key]));
            }
            await request(meta.put({ key: 'version', value: next }));
            return next;
        }, signal), signal);
    }

    openQuery(query: RecordQuery, signal?: AbortSignal): Promise<QueryReader<StoredRecord>> {
        const range = keyRange(query);
        const reverse = query.reverse ?? false;
        return this.gate.run(async () => {
            const database = this.connection();
            const owner = this.owner!;
            const id = encodeBase64Url((this.options.random ?? systemRandom).bytes(16));
            await transaction(database, ['records', 'snapshots'], 'readwrite', async tx => {
                let index = 0;
                await scan(tx.objectStore('records'), range, reverse, cursor => {
                    tx.objectStore('snapshots').add({ owner, reader: id, index: index++, record: cursor.value as DiskRecord });
                });
            }, signal);
            const reader = new IndexedDbReader(database, owner, id, () => this.readers.delete(reader));
            this.readers.add(reader);
            return reader;
        }, signal);
    }

    dispose(): Promise<void> {
        return this.gate.run(async () => {
            if (this.disposed) return;
            this.disposed = true;
            const errors: unknown[] = [];
            for (const reader of this.readers) { try { await reader.dispose(); } catch (error) { errors.push(error); } }
            try { if (this.database && this.owner && !this.invalidated) await this.removeOwner(this.database, this.owner); }
            catch (error) { errors.push(error); }
            finally {
                this.database?.close();
                this.database = undefined;
                this.releaseOwner?.();
                await this.ownerLock;
            }
            if (errors.length) throw new AggregateError(errors, 'Failed to dispose IndexedDB storage.');
        });
    }

    private removeOwner(database: IDBDatabase, id: string, signal?: AbortSignal): Promise<void> {
        return transaction(database, ['owners', 'snapshots'], 'readwrite', async tx => {
            await request(tx.objectStore('snapshots').delete(IDBKeyRange.bound([id, ''], [id, '\uffff'])));
            await request(tx.objectStore('owners').delete(id));
        }, signal);
    }

    private lockName(owner: string): string { return `meshline:${this.name}:snapshots:${owner}`; }
    private assertOpen(): void {
        if (this.disposed) throw new Error('Store is disposed.');
        if (this.invalidated) throw new Error('Database was closed for an external schema change. Reopen the store.');
    }
    private connection(): IDBDatabase {
        this.assertOpen();
        if (!this.database) throw new Error('Store is not initialized.');
        return this.database;
    }

    private openDatabase(migrate: boolean, signal?: AbortSignal): Promise<IDBDatabase> {
        throwIfAborted(signal);
        const factory = this.options.indexedDB ?? globalThis.indexedDB;
        if (!factory) return Promise.reject(new Error('IndexedDB is unavailable.'));
        return new Promise((resolve, reject) => {
            const operation = factory.open(this.name, 1);
            let rejected = false;
            operation.onblocked = () => { rejected = true; reject(new Error('Database migration is blocked by another connection.')); };
            operation.onupgradeneeded = () => {
                if (!migrate || rejected || signal?.aborted) { operation.transaction!.abort(); return; }
                const database = operation.result;
                database.createObjectStore('meta', { keyPath: 'key' }).add({ key: 'version', value: 0 });
                database.createObjectStore('records', { keyPath: ['collection', 'key'] });
                database.createObjectStore('owners', { keyPath: 'id' });
                database.createObjectStore('snapshots', { keyPath: ['owner', 'reader', 'index'] });
            };
            operation.onerror = () => reject(signal?.aborted ? signal.reason : operation.error ?? new Error('Explicit database migration is required.'));
            operation.onsuccess = () => {
                if (rejected || signal?.aborted) { operation.result.close(); reject(signal?.reason ?? new Error('Database open failed.')); }
                else resolve(operation.result);
            };
        });
    }
}

class IndexedDbReader implements QueryReader<StoredRecord> {
    private readonly gate = new AsyncGate();
    private offset = 0;
    private disposed = false;

    constructor(private readonly database: IDBDatabase, private readonly owner: string, private readonly reader: string, private readonly released: () => void) {}

    readNext(count: number, signal?: AbortSignal): Promise<readonly StoredRecord[]> {
        requireBatchCount(count);
        return this.gate.run(async () => {
            if (this.disposed) throw new Error('Reader is disposed.');
            const values = await transaction(this.database, ['snapshots'], 'readonly', async tx => {
                const values: StoredRecord[] = [];
                await scan(tx.objectStore('snapshots'), IDBKeyRange.bound([this.owner, this.reader, this.offset], [this.owner, this.reader, Number.MAX_SAFE_INTEGER]), false, cursor => {
                    values.push(decode((cursor.value as SnapshotRow).record));
                    return values.length < count;
                });
                return values;
            }, signal);
            this.offset += values.length;
            return values;
        }, signal);
    }

    dispose(): Promise<void> {
        return this.gate.run(async () => {
            if (this.disposed) return;
            this.disposed = true;
            try {
                await transaction(this.database, ['snapshots'], 'readwrite', async tx => {
                    await request(tx.objectStore('snapshots').delete(IDBKeyRange.bound([this.owner, this.reader, 0], [this.owner, this.reader, Number.MAX_SAFE_INTEGER])));
                });
            } finally { this.released(); }
        });
    }
}
