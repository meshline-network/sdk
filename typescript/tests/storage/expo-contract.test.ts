import { DatabaseSync } from 'node:sqlite';
import { mkdtemp, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test } from 'vitest';
import { NativeSqliteStore, type SqliteDatabase, type SqliteRuntime } from '../../packages/expo/src/sqlite-store.js';
import { StateConflictError, type StoreMutation } from '@meshline/sdk';
import { removeTestDirectory } from '../support/temp.js';

// Real SQLite contract tests for the async engine. These do not claim Expo/Hermes runtime coverage.
class TestRuntime implements SqliteRuntime {
    opens = 0; closes = 0;
    afterRun: ((sql: string) => void) | undefined;
    afterRead: (() => void) | undefined;
    failPut = 0;
    constructor(readonly path: string) {}
    async exists(): Promise<boolean> { try { return (await stat(this.path)).isFile(); } catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false; throw error; } }
    async open(): Promise<SqliteDatabase> {
        this.opens++;
        const database = new DatabaseSync(this.path); const runtime = this;
        return {
            async execAsync(sql) { database.exec(sql); },
            async runAsync(sql, ...parameters) {
                if (sql.startsWith('INSERT INTO meshline_records') && runtime.failPut && --runtime.failPut === 0) throw new Error('injected native write failure');
                const result = database.prepare(sql).run(...parameters); runtime.afterRun?.(sql); return result;
            },
            async getFirstAsync<T>(sql: string, ...parameters: (string | number | null)[]) { return database.prepare(sql).get(...parameters) as T ?? null; },
            async getAllAsync<T>(sql: string, ...parameters: (string | number | null)[]) { const result = database.prepare(sql).all(...parameters) as T[]; runtime.afterRead?.(); return result; },
            async closeAsync() { database.close(); runtime.closes++; },
        };
    }
}
const binding = { context: 'neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70', accountId: 'neo:860833102:NgaxELHoZFpQWNwd74Fvq4wF3qz57WTfpp' };
const directories: string[] = []; const stores: NativeSqliteStore[] = [];
async function create(initialize = true) {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-expo-sqlite-')); directories.push(directory);
    const runtime = new TestRuntime(join(directory, 'state.sqlite')); const store = new NativeSqliteStore(runtime); stores.push(store);
    if (initialize) { await store.migrate(); await store.initialize(binding); }
    return { runtime, store };
}
const put = (key: string, value: number, collection = 'messages'): StoreMutation => ({ kind: 'put', key, collection, value: { value } });
afterEach(async () => { for (const store of stores.splice(0).reverse()) await store.dispose(); for (const directory of directories.splice(0)) await removeTestDirectory(directory); });

test('native store has explicit migration and a persistent network/account binding', async () => {
    const { runtime, store } = await create(false);
    expect(runtime.opens).toBe(0); await expect(store.initialize(binding)).rejects.toThrow('migrated'); expect(runtime.opens).toBe(0);
    await store.migrate(); await store.initialize(binding); expect((await store.read([])).version).toBe(0);
    await store.dispose();
    const reopened = new NativeSqliteStore(runtime); stores.push(reopened);
    await expect(reopened.initialize({ ...binding, context: binding.context.replace('860833102', '860833103') })).rejects.toThrow('bound');
    await reopened.initialize(binding);
    expect((await reopened.read([])).version).toBe(0);
});

test('records and cursors survive restart and stale revision cannot partially overwrite them', async () => {
    const { runtime, store } = await create();
    await store.commit(0, [put('001', 1), put('relay', 1, 'cursors')]);
    const second = new NativeSqliteStore(runtime); stores.push(second); await second.initialize(binding);
    const before = await second.read([{ collection: 'messages' }, { collection: 'cursors' }]);
    await expect(second.commit(0, [put('001', 2), put('relay', 2, 'cursors')])).rejects.toBeInstanceOf(StateConflictError);
    expect(await store.read([{ collection: 'messages' }, { collection: 'cursors' }])).toEqual(before);
    await store.dispose(); await second.dispose();
    const reopened = new NativeSqliteStore(runtime); stores.push(reopened); await reopened.initialize(binding);
    expect(await reopened.read([{ collection: 'messages' }, { collection: 'cursors' }])).toEqual(before);
});

test('native reader pins its WAL snapshot before first page and serializes concurrent paging', async () => {
    const { runtime, store } = await create(); await store.commit(0, [put('001', 1), put('002', 2), put('003', 3)]);
    const reader = await store.openQuery({ collection: 'messages' });
    await store.commit(1, [put('001', 9), { kind: 'delete', collection: 'messages', key: '002' }, put('004', 4)]);
    const pages = await Promise.all([reader.readNext(1), reader.readNext(1), reader.readNext(10)]);
    expect(pages.flat().map(row => row.value)).toEqual([{ value: 1 }, { value: 2 }, { value: 3 }]);
    expect(await reader.readNext(1)).toEqual([]); await reader.dispose(); await store.dispose();
    expect(runtime.closes).toBe(runtime.opens);
});

test('native driver failure midway rolls back records and global version', async () => {
    const { runtime, store } = await create(); runtime.failPut = 2;
    await expect(store.commit(0, [put('001', 1), put('relay', 1, 'cursors')])).rejects.toThrow('injected');
    expect(await store.read([{ collection: 'messages' }, { collection: 'cursors' }])).toEqual({ version: 0, sets: [[], []] });
    await store.commit(0, [put('001', 1)]); expect((await store.read([])).version).toBe(1);
});

test('cancellation after a native write rolls back; cancellation after page fetch does not advance reader', async () => {
    const { runtime, store } = await create(); const cancelWrite = new AbortController();
    runtime.afterRun = sql => { if (sql.startsWith('INSERT INTO meshline_records')) cancelWrite.abort(new Error('canceled write')); };
    await expect(store.commit(0, [put('001', 1), put('002', 2)], cancelWrite.signal)).rejects.toThrow('canceled write');
    runtime.afterRun = undefined; expect((await store.read([])).version).toBe(0);
    await store.commit(0, [put('001', 1), put('002', 2)]);
    const reader = await store.openQuery({ collection: 'messages' }); const cancelRead = new AbortController();
    runtime.afterRead = () => cancelRead.abort(new Error('canceled read'));
    await expect(reader.readNext(1, cancelRead.signal)).rejects.toThrow('canceled read'); runtime.afterRead = undefined;
    expect((await reader.readNext(1))[0]!.key).toBe('001'); await reader.dispose();
});

test('prefix selection treats SQL wildcard characters literally and reverse order is stable', async () => {
    const { store } = await create(); await store.commit(0, [put('a_1', 1), put('a_2', 2), put('ab1', 3), put('a_1', 4, 'other')]);
    const reader = await store.openQuery({ collection: 'messages', prefix: 'a_', reverse: true });
    expect((await reader.readNext(10)).map(row => row.key)).toEqual(['a_2', 'a_1']); await reader.dispose();
    const pending = store.dispose(); expect(store.dispose()).toBe(pending); await pending;
    expect(() => store.read([])).toThrow('disposed');
});
