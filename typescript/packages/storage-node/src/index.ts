import { DatabaseSync, type SQLInputValue } from 'node:sqlite';
import { resolve } from 'node:path';
import { statSync } from 'node:fs';
import {
    StateConflictError, bindingJson, canonicalJson, parseJson, prepareCommit,
    requireBatchCount, requireObject, throwIfAborted, validateRecordQuery,
    type MeshlineStore, type QueryReader, type RecordQuery, type StorageBinding,
    type StoredRecord, type StoreMutation, type StoreSnapshot,
} from '@meshline/sdk';

interface Row { collection: string; key: string; revision: number; value: string }

function decodeRow(row: Row): StoredRecord {
    return { collection: row.collection, key: row.key, revision: row.revision, value: requireObject(parseJson(row.value)) };
}

function selection(query: RecordQuery): { where: string; parameters: SQLInputValue[]; order: string } {
    validateRecordQuery(query);
    const parameters: SQLInputValue[] = [query.collection];
    let where = 'collection = ?';
    if (query.key !== undefined) { where += ' AND key = ?'; parameters.push(query.key); }
    else if (query.prefix !== undefined) { where += ' AND key >= ? AND key < ?'; parameters.push(query.prefix, query.prefix + '\uffff'); }
    return { where, parameters, order: query.reverse ? 'DESC' : 'ASC' };
}

/** SQLite WAL storage with explicit migration, account binding, and fixed readers. */
export class NodeSqliteStore implements MeshlineStore {
    readonly path: string;
    private database: DatabaseSync | undefined;
    private initialized = false;
    private disposed = false;
    private disposal: Promise<void> | undefined;
    private readonly readers = new Set<SqliteReader>();

    constructor(path: string) {
        if (!path.trim() || path === ':memory:') throw new TypeError('A persistent SQLite file path is required.');
        this.path = resolve(path);
    }

    async migrate(signal?: AbortSignal): Promise<void> {
        this.assertOpen();
        throwIfAborted(signal);
        if (this.database) throw new Error('Migrate before initializing the store.');
        const database = new DatabaseSync(this.path);
        try {
            database.exec('PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; BEGIN IMMEDIATE;');
            try {
                const version = (database.prepare('PRAGMA user_version').get() as { user_version: number }).user_version;
                if (version > 1) throw new Error(`Unsupported database schema ${version}.`);
                if (version === 0) database.exec(`
                    CREATE TABLE meshline_meta (id INTEGER PRIMARY KEY CHECK(id = 1), binding TEXT, version INTEGER NOT NULL CHECK(version >= 0));
                    INSERT INTO meshline_meta VALUES (1, NULL, 0);
                    CREATE TABLE meshline_records (collection TEXT NOT NULL, key TEXT NOT NULL, revision INTEGER NOT NULL, value TEXT NOT NULL,
                        PRIMARY KEY(collection, key)) WITHOUT ROWID;
                    PRAGMA user_version = 1;
                `);
                throwIfAborted(signal);
                database.exec('COMMIT');
            } catch (error) { database.exec('ROLLBACK'); throw error; }
        } finally { database.close(); }
    }

    async initialize(binding: StorageBinding, signal?: AbortSignal): Promise<void> {
        this.assertOpen();
        throwIfAborted(signal);
        const encoded = bindingJson(binding);
        if (this.initialized) {
            const existing = this.connection().prepare('SELECT binding FROM meshline_meta WHERE id = 1').get() as { binding: string };
            if (existing.binding !== encoded) throw new Error('Database is bound to another network or account.');
            return;
        }
        if (!statSync(this.path).isFile()) throw new Error('Database must be explicitly migrated before initialization.');
        const database = new DatabaseSync(this.path, { open: true, readOnly: false });
        try {
            const version = (database.prepare('PRAGMA user_version').get() as { user_version: number }).user_version;
            if (version !== 1) throw new Error('Database must be explicitly migrated before initialization.');
            database.exec('PRAGMA busy_timeout = 5000; PRAGMA synchronous = FULL; BEGIN IMMEDIATE;');
            try {
                const current = database.prepare('SELECT binding FROM meshline_meta WHERE id = 1').get() as { binding: string | null };
                if (current.binding !== null && current.binding !== encoded) throw new Error('Database is bound to another network or account.');
                if (current.binding === null) database.prepare('UPDATE meshline_meta SET binding = ? WHERE id = 1').run(encoded);
                throwIfAborted(signal);
                database.exec('COMMIT');
            } catch (error) { database.exec('ROLLBACK'); throw error; }
            this.database = database;
            this.initialized = true;
        } catch (error) { database.close(); throw error; }
    }

    async read(queries: readonly RecordQuery[], signal?: AbortSignal): Promise<StoreSnapshot> {
        const selections = queries.map(selection);
        throwIfAborted(signal);
        const database = this.connection();
        database.exec('BEGIN DEFERRED');
        try {
            const version = (database.prepare('SELECT version FROM meshline_meta WHERE id = 1').get() as { version: number }).version;
            const sets = selections.map(query => (database.prepare(`SELECT * FROM meshline_records WHERE ${query.where} ORDER BY key ${query.order}`)
                .all(...query.parameters) as unknown as Row[]).map(decodeRow));
            throwIfAborted(signal);
            database.exec('COMMIT');
            return { version, sets };
        } catch (error) { database.exec('ROLLBACK'); throw error; }
    }

    async commit(expectedVersion: number, mutations: readonly StoreMutation[], signal?: AbortSignal): Promise<number> {
        const changes = prepareCommit(expectedVersion, mutations);
        throwIfAborted(signal);
        const database = this.connection();
        database.exec('BEGIN IMMEDIATE');
        try {
            const version = (database.prepare('SELECT version FROM meshline_meta WHERE id = 1').get() as { version: number }).version;
            if (version !== expectedVersion) throw new StateConflictError('Database revision changed before commit.');
            const next = expectedVersion + 1;
            const put = database.prepare('INSERT INTO meshline_records VALUES (?, ?, ?, ?) ON CONFLICT(collection, key) DO UPDATE SET revision = excluded.revision, value = excluded.value');
            const remove = database.prepare('DELETE FROM meshline_records WHERE collection = ? AND key = ?');
            for (const change of changes) {
                if (change.kind === 'put') put.run(change.collection, change.key, next, canonicalJson(change.value));
                else remove.run(change.collection, change.key);
            }
            database.prepare('UPDATE meshline_meta SET version = ? WHERE id = 1').run(next);
            throwIfAborted(signal);
            database.exec('COMMIT');
            return next;
        } catch (error) { database.exec('ROLLBACK'); throw error; }
    }

    async openQuery(query: RecordQuery, signal?: AbortSignal): Promise<QueryReader<StoredRecord>> {
        this.connection();
        const selected = selection(query);
        throwIfAborted(signal);
        const database = new DatabaseSync(this.path, { readOnly: true });
        try {
            database.exec('PRAGMA busy_timeout = 5000; BEGIN DEFERRED;');
            // Fix the WAL snapshot now, not on the first application read.
            database.prepare('SELECT version FROM meshline_meta WHERE id = 1').get();
            const reader = new SqliteReader(database, selected, () => this.readers.delete(reader));
            this.readers.add(reader);
            return reader;
        } catch (error) { database.close(); throw error; }
    }

    dispose(): Promise<void> {
        if (this.disposal) return this.disposal;
        this.disposed = true;
        this.disposal = (async () => {
            const errors: unknown[] = [];
            for (const reader of this.readers) { try { await reader.dispose(); } catch (error) { errors.push(error); } }
            try { this.database?.close(); } catch (error) { errors.push(error); }
            this.database = undefined;
            if (errors.length) throw new AggregateError(errors, 'Failed to dispose SQLite storage.');
        })();
        return this.disposal;
    }

    private assertOpen(): void { if (this.disposed) throw new Error('Store is disposed.'); }
    private connection(): DatabaseSync {
        this.assertOpen();
        if (!this.initialized || !this.database) throw new Error('Store is not initialized.');
        return this.database;
    }
}

class SqliteReader implements QueryReader<StoredRecord> {
    private offset = 0;
    private disposed = false;
    private completed = false;

    constructor(private readonly database: DatabaseSync, private readonly query: ReturnType<typeof selection>, private readonly released: () => void) {}

    async readNext(count: number, signal?: AbortSignal): Promise<readonly StoredRecord[]> {
        if (this.disposed) throw new Error('Reader is disposed.');
        requireBatchCount(count);
        throwIfAborted(signal);
        if (this.completed) return [];
        const rows = this.database.prepare(`SELECT * FROM meshline_records WHERE ${this.query.where} ORDER BY key ${this.query.order} LIMIT ? OFFSET ?`)
            .all(...this.query.parameters, count, this.offset) as unknown as Row[];
        const values = rows.map(decodeRow);
        throwIfAborted(signal);
        this.offset += values.length;
        this.completed = values.length < count;
        return values;
    }

    async dispose(): Promise<void> {
        if (this.disposed) return;
        this.disposed = true;
        try { this.database.exec('ROLLBACK'); }
        finally { try { this.database.close(); } finally { this.released(); } }
    }
}
