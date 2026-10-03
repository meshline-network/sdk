import {
    AsyncGate, StateConflictError, bindingJson, canonicalJson, parseJson, prepareCommit, requireBatchCount,
    requireObject, throwIfAborted, validateRecordQuery, type MeshlineStore, type QueryReader, type RecordQuery,
    type StorageBinding, type StoredRecord, type StoreMutation, type StoreSnapshot,
} from '@meshline/sdk';

/** Private adapter boundary, also exercised against a real SQLite driver in contract tests. */
export interface SqliteDatabase {
    execAsync(sql: string): Promise<void>;
    runAsync(sql: string, ...parameters: (string | number | null)[]): Promise<unknown>;
    getFirstAsync<T>(sql: string, ...parameters: (string | number | null)[]): Promise<T | null>;
    getAllAsync<T>(sql: string, ...parameters: (string | number | null)[]): Promise<T[]>;
    closeAsync(): Promise<void>;
}
export interface SqliteRuntime {
    exists(): Promise<boolean>;
    /** Every call MUST return a new, private native connection. Never use Expo's shared connection cache. */
    open(): Promise<SqliteDatabase>;
}
interface Row { collection: string; key: string; revision: number; value: string }
function decodeRow(row: Row): StoredRecord { return { collection: row.collection, key: row.key, revision: row.revision, value: requireObject(parseJson(row.value)) }; }
function selection(query: RecordQuery) {
    validateRecordQuery(query);
    let where = 'collection = ?'; const parameters: (string | number | null)[] = [query.collection];
    if (query.key !== undefined) { where += ' AND key = ?'; parameters.push(query.key); }
    else if (query.prefix !== undefined) { where += ' AND key >= ? AND key < ?'; parameters.push(query.prefix, query.prefix + '\uffff'); }
    return { where, parameters, order: query.reverse ? 'DESC' : 'ASC' };
}

/** Serialized private writer and independent WAL snapshots, with no transaction spanning application callbacks. */
export class NativeSqliteStore implements MeshlineStore {
    readonly #gate = new AsyncGate();
    readonly #readers = new Set<NativeReader>();
    #database: SqliteDatabase | undefined;
    #disposed = false;
    #disposal: Promise<void> | undefined;
    constructor(readonly runtime: SqliteRuntime) {}

    migrate(signal?: AbortSignal): Promise<void> {
        this.#check();
        return this.#gate.run(async () => {
            this.#check(); throwIfAborted(signal);
            if (this.#database) throw new Error('Migrate before initializing storage.');
            const database = await this.runtime.open();
            try {
                await database.execAsync('PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;');
                await transaction(database, 'IMMEDIATE', async () => {
                    const version = (await database.getFirstAsync<{ user_version: number }>('PRAGMA user_version'))!.user_version;
                    if (version > 1) throw new Error(`Unsupported database schema ${version}.`);
                    if (version === 0) await database.execAsync(`
                        CREATE TABLE meshline_meta (id INTEGER PRIMARY KEY CHECK(id = 1), binding TEXT, version INTEGER NOT NULL CHECK(version >= 0));
                        INSERT INTO meshline_meta VALUES (1, NULL, 0);
                        CREATE TABLE meshline_records (collection TEXT NOT NULL, key TEXT NOT NULL, revision INTEGER NOT NULL, value TEXT NOT NULL,
                            PRIMARY KEY(collection, key)) WITHOUT ROWID;
                        PRAGMA user_version = 1;
                    `);
                }, signal);
            } finally { await database.closeAsync(); }
        }, signal);
    }

    initialize(binding: StorageBinding, signal?: AbortSignal): Promise<void> {
        this.#check(); const encoded = bindingJson(binding);
        return this.#gate.run(async () => {
            this.#check(); throwIfAborted(signal);
            if (this.#database) {
                const existing = await this.#database.getFirstAsync<{ binding: string }>('SELECT binding FROM meshline_meta WHERE id = 1');
                if (existing?.binding !== encoded) throw new Error('Database is bound to another network or account.');
                return;
            }
            if (!await this.runtime.exists()) throw new Error('Database must be explicitly migrated before initialization.');
            const database = await this.runtime.open();
            try {
                const version = (await database.getFirstAsync<{ user_version: number }>('PRAGMA user_version'))!.user_version;
                if (version !== 1) throw new Error('Database must be explicitly migrated before initialization.');
                await database.execAsync('PRAGMA busy_timeout = 5000; PRAGMA synchronous = FULL;');
                await transaction(database, 'IMMEDIATE', async () => {
                    const current = await database.getFirstAsync<{ binding: string | null }>('SELECT binding FROM meshline_meta WHERE id = 1');
                    if (!current) throw new Error('Missing Meshline database metadata.');
                    if (current.binding !== null && current.binding !== encoded) throw new Error('Database is bound to another network or account.');
                    if (current.binding === null) await database.runAsync('UPDATE meshline_meta SET binding = ? WHERE id = 1', encoded);
                }, signal);
                this.#database = database;
            } catch (error) { await database.closeAsync(); throw error; }
        }, signal);
    }

    read(queries: readonly RecordQuery[], signal?: AbortSignal): Promise<StoreSnapshot> {
        this.#check(); const selected = queries.map(selection);
        return this.#gate.run(async () => {
            const database = this.#connection();
            return transaction(database, 'DEFERRED', async () => {
                const version = (await database.getFirstAsync<{ version: number }>('SELECT version FROM meshline_meta WHERE id = 1'))!.version;
                const sets: StoredRecord[][] = [];
                for (const query of selected) sets.push((await database.getAllAsync<Row>(`SELECT * FROM meshline_records WHERE ${query.where} ORDER BY key ${query.order}`, ...query.parameters)).map(decodeRow));
                return { version, sets };
            }, signal);
        }, signal);
    }

    commit(expectedVersion: number, mutations: readonly StoreMutation[], signal?: AbortSignal): Promise<number> {
        this.#check(); const changes = prepareCommit(expectedVersion, mutations);
        return this.#gate.run(async () => {
            const database = this.#connection();
            return transaction(database, 'IMMEDIATE', async () => {
                const version = (await database.getFirstAsync<{ version: number }>('SELECT version FROM meshline_meta WHERE id = 1'))!.version;
                if (version !== expectedVersion) throw new StateConflictError('Database revision changed before commit.');
                const next = expectedVersion + 1;
                for (const change of changes) {
                    throwIfAborted(signal);
                    if (change.kind === 'put') await database.runAsync('INSERT INTO meshline_records VALUES (?, ?, ?, ?) ON CONFLICT(collection, key) DO UPDATE SET revision = excluded.revision, value = excluded.value', change.collection, change.key, next, canonicalJson(change.value));
                    else await database.runAsync('DELETE FROM meshline_records WHERE collection = ? AND key = ?', change.collection, change.key);
                }
                await database.runAsync('UPDATE meshline_meta SET version = ? WHERE id = 1', next);
                return next;
            }, signal);
        }, signal);
    }

    openQuery(query: RecordQuery, signal?: AbortSignal): Promise<QueryReader<StoredRecord>> {
        this.#check(); const selected = selection(query);
        return this.#gate.run(async () => {
            this.#connection(); throwIfAborted(signal);
            const database = await this.runtime.open();
            try {
                await database.execAsync('PRAGMA busy_timeout = 5000; PRAGMA query_only = ON; BEGIN DEFERRED;');
                // Reading a real table pins the WAL snapshot before returning to application code.
                await database.getFirstAsync('SELECT version FROM meshline_meta WHERE id = 1');
                throwIfAborted(signal);
                const reader = new NativeReader(database, selected, () => this.#readers.delete(reader));
                this.#readers.add(reader); return reader;
            } catch (error) { await database.closeAsync(); throw error; }
        }, signal);
    }

    #check(): void { if (this.#disposed) throw new Error('Store is disposed.'); }
    #connection(): SqliteDatabase { this.#check(); if (!this.#database) throw new Error('Store is not initialized.'); return this.#database; }
    dispose(): Promise<void> {
        this.#disposed = true;
        return this.#disposal ??= this.#gate.run(async () => {
            const failures: unknown[] = [];
            for (const reader of this.#readers) { try { await reader.dispose(); } catch (error) { failures.push(error); } }
            try { await this.#database?.closeAsync(); } catch (error) { failures.push(error); }
            this.#database = undefined;
            if (failures.length) throw new AggregateError(failures, 'Failed to dispose native SQLite storage.');
        });
    }
}

async function transaction<T>(database: SqliteDatabase, mode: 'IMMEDIATE' | 'DEFERRED', action: () => Promise<T>, signal?: AbortSignal): Promise<T> {
    throwIfAborted(signal); await database.execAsync(`BEGIN ${mode}`);
    try {
        const result = await action(); throwIfAborted(signal);
        await database.execAsync('COMMIT'); return result;
    } catch (error) {
        try { await database.execAsync('ROLLBACK'); }
        catch (rollbackError) { throw new AggregateError([error, rollbackError], 'SQLite operation and rollback both failed.'); }
        throw error;
    }
}

class NativeReader implements QueryReader<StoredRecord> {
    readonly #gate = new AsyncGate();
    #offset = 0;
    #completed = false;
    #disposed = false;
    #disposal: Promise<void> | undefined;
    constructor(readonly database: SqliteDatabase, readonly query: ReturnType<typeof selection>, readonly released: () => void) {}
    readNext(count: number, signal?: AbortSignal): Promise<readonly StoredRecord[]> {
        requireBatchCount(count);
        return this.#gate.run(async () => {
            if (this.#disposed) throw new Error('Reader is disposed.');
            throwIfAborted(signal); if (this.#completed) return [];
            const rows = await this.database.getAllAsync<Row>(`SELECT * FROM meshline_records WHERE ${this.query.where} ORDER BY key ${this.query.order} LIMIT ? OFFSET ?`, ...this.query.parameters, count, this.#offset);
            const records = rows.map(decodeRow); throwIfAborted(signal);
            this.#offset += records.length; this.#completed = records.length < count;
            return records;
        }, signal);
    }
    dispose(): Promise<void> {
        this.#disposed = true;
        return this.#disposal ??= this.#gate.run(async () => {
            try { await this.database.execAsync('ROLLBACK'); }
            finally { try { await this.database.closeAsync(); } finally { this.released(); } }
        });
    }
}
