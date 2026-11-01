import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, expect, test, vi } from 'vitest';
import { NodeSqliteStore } from '@meshline/storage-node';
import { RelayError, StateUpdateSupersededError, getAccountId, accountPublicKey, type JsonObject, type MeshlineStore, type StoredRecord } from '@meshline/sdk';
import { StateUpdateCoordinator, type StateUpdateAttempt } from '../../packages/sdk/dist/components/state-update-coordinator.js';
import { MessagingClock } from '../portable/clock.js';
import { removeTestDirectory } from '../support/temp.js';

function deferred() { let resolve!: () => void; const promise = new Promise<void>(done => { resolve = done; }); return { promise, resolve }; }
const cleanup: (() => Promise<void>)[] = [];
afterEach(async () => { for (const action of cleanup.splice(0).reverse()) await action(); });

class Updates extends StateUpdateCoordinator<JsonObject> {
    protected readonly operation = 'test';
    protected readonly query = { collection: 'signed_requests' };
    readonly controller = new AbortController();
    readonly sent: { resource: string; intent: JsonObject }[] = [];
    readonly failures: unknown[] = [];
    beforeSend: ((attempt: StateUpdateAttempt<JsonObject>) => Promise<void>) | undefined;
    afterSend: ((attempt: StateUpdateAttempt<JsonObject>) => Promise<void>) | undefined;
    protected key(resource: string) { return { collection: 'signed_requests', key: resource }; }
    protected resource(row: StoredRecord) { return row.key; }
    protected create(): JsonObject { return {}; }
    protected report(_resource: string, error: unknown) { this.failures.push(error); }
    async publish(attempt: StateUpdateAttempt<JsonObject>, signal: AbortSignal): Promise<JsonObject> {
        await this.beforeSend?.(attempt);
        await attempt.saveRequest(attempt.intent, {}, signal);
        this.sent.push({ resource: attempt.resource, intent: attempt.intent });
        await this.afterSend?.(attempt);
        await attempt.check(signal);
        return attempt.intent;
    }
    send(patch: JsonObject, resource = 'one', signal = this.controller.signal) { return this.submit(resource, patch, signal); }
    run() { this.start(this.controller.signal); }
    async dispose() { this.controller.abort(); await this.stop(); await this.drain(); }
}
async function fixture(path?: string) {
    if (!path) { const directory = await mkdtemp(join(tmpdir(), 'meshline-state-')); cleanup.push(() => removeTestDirectory(directory)); path = join(directory, 'state.sqlite'); }
    const store = new NodeSqliteStore(path); cleanup.push(() => store.dispose()); await store.migrate(); await store.initialize({ context: 'neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70', accountId: getAccountId('neo:860833102', accountPublicKey(new Uint8Array(32).fill(3))) });
    const clock = new MessagingClock(); const updates = new Updates(store, clock); cleanup.push(() => updates.dispose());
    const row = async (resource = 'one') => (await store.read([{ collection: 'signed_requests', key: resource }])).sets[0]![0]?.value;
    return { store, clock, updates, row, path };
}

test.each(['beforeSend', 'afterSend'] as const)('a successor completes while an uncancellable predecessor waits in %s', async stage => {
    const f = await fixture(); const entered = deferred(); const release = deferred();
    f.updates[stage] = async attempt => { if (attempt.generation === 1) { entered.resolve(); await release.promise; } };
    const old = f.updates.send({ name: 'old', bio: 'kept', avatar: 'old-avatar' });
    const superseded = expect(old).rejects.toBeInstanceOf(StateUpdateSupersededError);
    try {
        await entered.promise;
        expect(await f.updates.send({ name: 'new', avatar: null })).toEqual({ name: 'new', bio: 'kept', avatar: null });
        await superseded;
        expect(await f.row()).toMatchObject({ generation: 2, pending: false, intent: null, document: { name: 'new', bio: 'kept', avatar: null } });
        release.resolve(); await f.updates.drain();
        expect((await f.row())!.document).toEqual({ name: 'new', bio: 'kept', avatar: null });
    } finally { release.resolve(); }
});

test('a failed admission leaves the active call and its durable intent intact', async () => {
    const f = await fixture(); const entered = deferred(); const release = deferred();
    f.updates.afterSend = async () => { entered.resolve(); await release.promise; };
    const old = f.updates.send({ name: 'old' });
    try {
        await entered.promise;
        const saved = await f.row(); const commit = vi.spyOn(f.store, 'commit').mockRejectedValueOnce(new Error('disk full'));
        await expect(f.updates.send({ name: 'new' })).rejects.toThrow('disk full'); commit.mockRestore();
        expect(await f.row()).toEqual(saved);
        release.resolve(); expect(await old).toEqual({ name: 'old' });
    } finally { release.resolve(); }
});

test('cancellation preserves merged intent for restart and a late completion cannot clear it', async () => {
    const f = await fixture(); const entered = deferred(); const release = deferred(); const caller = new AbortController();
    f.updates.afterSend = async () => { entered.resolve(); await release.promise; };
    const old = f.updates.send({ name: 'old', bio: 'retained' }); const superseded = expect(old).rejects.toBeInstanceOf(StateUpdateSupersededError);
    try {
        await entered.promise;
        f.updates.beforeSend = async attempt => { if (attempt.generation === 2) caller.abort(new Error('caller canceled')); };
        await expect(f.updates.send({ name: 'latest' }, 'one', caller.signal)).rejects.toThrow('caller canceled');
        await superseded; release.resolve(); await f.updates.dispose();
        expect(await f.row()).toMatchObject({ pending: true, intent: { name: 'latest', bio: 'retained' } });
        await f.store.dispose();
        const resumed = await fixture(f.path); resumed.clock.tick(); resumed.updates.run();
        await vi.waitFor(async () => expect((await resumed.row())!.pending).toBe(false));
        expect(resumed.updates.sent).toEqual([{ resource: 'one', intent: { name: 'latest', bio: 'retained' } }]);
    } finally { release.resolve(); }
});

test('retry deadlines survive restart and long relay delays use bounded timers', async () => {
    const f = await fixture(); f.updates.afterSend = async () => { throw new RelayError({ code: 'rate_limited', message: 'wait', data: { retry_after: 100_000 } }); };
    await expect(f.updates.send({ name: 'latest' })).rejects.toThrow('wait');
    const deadline = f.clock.wall + 100_000; expect((await f.row())!.nextAttemptAt).toBe(deadline);
    await f.updates.dispose(); await f.store.dispose(); const resumed = await fixture(f.path); resumed.updates.run();
    await vi.waitFor(() => expect(resumed.clock.waiting.size).toBe(1));
    expect([...resumed.clock.waiting][0]!.milliseconds).toBe(60_000); expect(resumed.updates.sent).toEqual([]);
    resumed.clock.wall = deadline; for (const wait of [...resumed.clock.waiting]) wait.complete();
    await vi.waitFor(async () => expect((await resumed.row())!.pending).toBe(false)); expect(resumed.updates.sent).toHaveLength(1);
});

test('permanent failures remain paused across restart until a new call merges the intent', async () => {
    const f = await fixture(); f.updates.afterSend = async () => { throw new RelayError({ code: 'forbidden', message: 'denied' }); };
    await expect(f.updates.send({ name: 'old', bio: 'kept' })).rejects.toThrow('denied'); expect((await f.row())!.nextAttemptAt).toBeNull();
    await f.updates.dispose(); await f.store.dispose(); const resumed = await fixture(f.path); resumed.updates.run();
    expect(await resumed.updates.send({ name: 'new' })).toEqual({ name: 'new', bio: 'kept' });
    expect(resumed.updates.sent).toHaveLength(1);
});

test.each([false, true])('aggregate discovery failures retry only when every cause is transient (permanent=%s)', async permanent => {
    const f = await fixture();
    f.updates.beforeSend = async () => { throw new AggregateError([new TypeError('offline'), permanent
        ? new RelayError({ code: 'invalid_signature', message: 'invalid relay' }) : new TypeError('second relay offline')]); };
    await expect(f.updates.send({ name: 'latest' })).rejects.toBeInstanceOf(AggregateError);
    expect((await f.row())!.nextAttemptAt).toBe(permanent ? null : f.clock.wall + 1);
    if (!permanent) {
        f.updates.beforeSend = undefined; f.clock.tick(); f.updates.run();
        await vi.waitFor(async () => expect((await f.row())!.pending).toBe(false));
    }
});

test('a failed retry checkpoint cannot spin on an expired durable deadline', async () => {
    const f = await fixture(); const commit = f.store.commit.bind(f.store); let rejectCheckpoint = false;
    vi.spyOn(f.store, 'commit').mockImplementation(async (...args: Parameters<MeshlineStore['commit']>) => {
        if (rejectCheckpoint) { rejectCheckpoint = false; throw new Error('disk unavailable'); }
        return commit(...args);
    });
    f.updates.afterSend = async () => { rejectCheckpoint = true; throw new TypeError('network unavailable'); };
    await expect(f.updates.send({ name: 'latest' })).rejects.toBeInstanceOf(AggregateError);
    f.updates.run(); await vi.waitFor(() => expect(f.clock.waiting.size).toBe(1));
    expect(f.updates.sent).toHaveLength(1); expect([...f.clock.waiting][0]!.milliseconds).toBe(1000);
    f.updates.afterSend = undefined; f.clock.tick();
    await vi.waitFor(async () => expect((await f.row())!.pending).toBe(false));
});

test('one blocked resource does not block another and disposal drains uncancellable work', async () => {
    const f = await fixture(); const entered = deferred(); const release = deferred();
    f.updates.beforeSend = async attempt => { if (attempt.resource === 'one') { entered.resolve(); await release.promise; } };
    const old = f.updates.send({ name: 'old' }); const aborted = expect(old).rejects.toMatchObject({ name: 'AbortError' });
    try {
        await entered.promise; expect(await f.updates.send({ name: 'independent' }, 'two')).toEqual({ name: 'independent' });
        let disposed = false; const disposing = f.updates.dispose().then(() => { disposed = true; });
        await aborted; expect(disposed).toBe(false); release.resolve(); await disposing;
        expect(f.updates.sent.map(value => value.resource)).toEqual(['two']);
    } finally { release.resolve(); }
});
