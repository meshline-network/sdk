import { mkdtemp, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test, vi } from 'vitest';
import { NodeSqliteStore } from '../../packages/storage-node/src/index.js';
import { StateConflictError, type StoreMutation } from '@meshline/sdk';
import { removeTestDirectory } from '../support/temp.js';

const binding = { context: 'neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70', accountId: 'neo:860833102:NgaxELHoZFpQWNwd74Fvq4wF3qz57WTfpp' };
const owned: NodeSqliteStore[] = [];
const directories: string[] = [];

async function store(path?: string): Promise<NodeSqliteStore> {
    if (!path) {
        const directory = await mkdtemp(join(tmpdir(), 'meshline-ts-'));
        directories.push(directory);
        path = join(directory, 'state.sqlite');
    }
    const result = new NodeSqliteStore(path);
    owned.push(result);
    await result.migrate();
    await result.initialize(binding);
    return result;
}

afterEach(async () => {
    for (const instance of owned.splice(0).reverse()) await instance.dispose();
    for (const directory of directories.splice(0)) await removeTestDirectory(directory);
});

const put = (key: string, value: number, collection = 'messages'): StoreMutation => ({ kind: 'put', collection, key, value: { value } });

test('constructors do no I/O and initialization never implicitly migrates', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-ts-'));
    directories.push(directory);
    const path = join(directory, 'state.sqlite');
    const instance = new NodeSqliteStore(path);
    owned.push(instance);
    await expect(stat(path)).rejects.toMatchObject({ code: 'ENOENT' });
    await expect(instance.initialize(binding)).rejects.toThrow();
    await expect(stat(path)).rejects.toMatchObject({ code: 'ENOENT' });
    await instance.migrate();
    await instance.initialize(binding);
    expect((await instance.read([])).version).toBe(0);
});

test('messages and synchronization cursor commit atomically and survive restart', async () => {
    const first = await store();
    await first.commit(0, [put('001', 1), put('relay', 1, 'cursors')]);
    const before = await first.read([{ collection: 'messages' }, { collection: 'cursors' }]);
    expect(before.version).toBe(1);
    expect(before.sets.map(rows => rows[0]!.value)).toEqual([{ value: 1 }, { value: 1 }]);
    await first.dispose();
    const reopened = new NodeSqliteStore(first.path);
    owned.push(reopened);
    await reopened.initialize(binding);
    expect(await reopened.read([{ collection: 'messages' }, { collection: 'cursors' }])).toEqual(before);
});

test('concurrent disposal waits for the same reader and database cleanup', async () => {
    const instance = await store(); const reader = await instance.openQuery({ collection: 'messages' });
    const original = reader.dispose.bind(reader); let release!: () => void;
    const waiting = new Promise<void>(resolve => { release = resolve; });
    vi.spyOn(reader, 'dispose').mockImplementation(async () => { await waiting; await original(); });
    const first = instance.dispose(); const second = instance.dispose(); let finished = false;
    void second.then(() => { finished = true; }); await Promise.resolve(); expect(finished).toBe(false); expect(second).toBe(first);
    await expect(instance.read([])).rejects.toThrow('disposed'); release(); await Promise.all([first, second]);
    await expect(reader.readNext(1)).rejects.toThrow('disposed'); expect(reader.dispose).toHaveBeenCalledOnce();
});

test('a stale writer cannot partially overwrite state and cursor', async () => {
    const first = await store();
    const second = new NodeSqliteStore(first.path);
    owned.push(second);
    await second.initialize(binding);
    const snapshot = await second.read([]);
    await first.commit(0, [put('001', 1), put('relay', 1, 'cursors')]);
    await expect(second.commit(snapshot.version, [put('001', 2), put('relay', 2, 'cursors')])).rejects.toBeInstanceOf(StateConflictError);
    const after = await first.read([{ collection: 'messages' }, { collection: 'cursors' }]);
    expect(after.version).toBe(1);
    expect(after.sets.map(rows => rows[0]!.value)).toEqual([{ value: 1 }, { value: 1 }]);
});

test('fixed query snapshots exclude inserts, preserve deleted rows and retain old values', async () => {
    const instance = await store();
    await instance.commit(0, [put('001', 1), put('002', 2), put('003', 3)]);
    const reader = await instance.openQuery({ collection: 'messages' });
    // Mutation occurs before the reader's first batch.
    await instance.commit(1, [put('001', 9), { kind: 'delete', collection: 'messages', key: '002' }, put('004', 4)]);
    expect((await reader.readNext(1)).map(row => row.value)).toEqual([{ value: 1 }]);
    expect((await reader.readNext(10)).map(row => row.value)).toEqual([{ value: 2 }, { value: 3 }]);
    expect(await reader.readNext(1)).toEqual([]);
    await reader.dispose();
    await expect(reader.readNext(1)).rejects.toThrow('disposed');
    const refreshed = await instance.openQuery({ collection: 'messages', reverse: true });
    expect((await refreshed.readNext(10)).map(row => row.key)).toEqual(['004', '003', '001']);
    await refreshed.dispose();
});

test('invalid mutations and canceled operations do not change stored state', async () => {
    const instance = await store();
    await expect(instance.commit(0, [put('001', 1), put('001', 2)])).rejects.toThrow('only once');
    await expect(instance.commit(0, [put('001', 1), put('002', NaN)])).rejects.toThrow();
    const abort = new AbortController();
    abort.abort(new Error('test canceled'));
    await expect(instance.commit(0, [put('001', 1)], abort.signal)).rejects.toThrow('test canceled');
    expect(await instance.read([{ collection: 'messages' }])).toEqual({ version: 0, sets: [[]] });
});

test('query selection isolates collections and treats prefixes literally', async () => {
    const instance = await store();
    await instance.commit(0, [put('a%001', 1), put('abc', 2), put('a%002', 3), put('a%001', 4, 'cursors')]);
    const result = await instance.read([{ collection: 'messages', prefix: 'a%', reverse: true }, { collection: 'cursors', key: 'a%001' }]);
    expect(result.sets[0]!.map(row => row.value.value)).toEqual([3, 1]);
    expect(result.sets[1]!.map(row => row.value.value)).toEqual([4]);
});

test('binding rejects a different network without resetting data', async () => {
    const instance = await store();
    await instance.commit(0, [put('001', 1)]);
    const other = new NodeSqliteStore(instance.path);
    owned.push(other);
    await expect(other.initialize({ ...binding, context: binding.context.replace('860833102', '860833103') })).rejects.toThrow('another');
    expect((await instance.read([{ collection: 'messages' }])).sets[0]).toHaveLength(1);
});

test('result mutation cannot mutate the database and disposal releases readers', async () => {
    const instance = await store();
    const source: StoreMutation = { kind: 'put', collection: 'messages', key: '001', value: { value: 1 } };
    await instance.commit(0, [source]);
    source.value.value = 2;
    const result = await instance.read([{ collection: 'messages' }]);
    result.sets[0]![0]!.value.value = 3;
    expect((await instance.read([{ collection: 'messages' }])).sets[0]![0]!.value.value).toBe(1);
    const reader = await instance.openQuery({ collection: 'messages' });
    await instance.dispose();
    await expect(reader.readNext(1)).rejects.toThrow('disposed');
    await expect(instance.commit(1, [put('002', 2)])).rejects.toThrow('disposed');
});
