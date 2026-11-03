import { afterEach, expect, test, vi } from 'vitest';
import {
    AccountEstablishmentError, RelayError, StateUpdateSupersededError, profileInput,
    type AccountProfile, type ProfileUpdateState,
} from '@meshline/sdk';
import { ClientNetwork } from '../support/client-network.js';
import { context } from '../support/relay-fixture.js';

const networks: ClientNetwork[] = [];
afterEach(async () => { vi.restoreAllMocks(); for (const network of networks.splice(0)) await network.dispose(); });
const progress = { collection: 'client_account_operations', key: 'establishment' };
const publication = { collection: 'signed_requests', key: 'profile.publish' };
function deferred() { let resolve!: () => void; const promise = new Promise<void>(done => { resolve = done; }); return { promise, resolve }; }
async function fixture(establish = true) {
    const network = new ClientNetwork(); networks.push(network);
    const opened = await network.open();
    if (establish) await opened.client.establishAccount({ initialProfile: { nickname: 'confirmed', bio: 'biography' } });
    network.requests.length = 0;
    return { network, ...opened };
}
async function remote(f: Awaited<ReturnType<typeof fixture>>, nickname = 'existing') {
    const value: AccountProfile = { account: f.client.accountId, nickname, bio: 'remote biography', publicDiscovery: true,
        updatedAt: f.network.clock.wall, deviceSignature: new Uint8Array(64) };
    const profile = { ...value, deviceSignature: await f.client.deviceManager.sign(profileInput(value, context)) };
    f.network.relays[0]!.profiles.set(f.client.accountId, { profile, signerCertificate: f.client.device! });
    return profile;
}

test('local state is available before start and does not imply a confirmed profile exists', async () => {
    const f = await fixture(false); const manager = f.client.profileManager;
    expect(await manager.getUpdateState()).toEqual({ status: 'synchronized' });
    expect(manager.profile).toBeUndefined(); expect(f.network.requests).toEqual([]);
    expect(await manager.publishProfile()).toBeUndefined();
    expect(await manager.getUpdateState()).toEqual({ status: 'synchronized' });
    const caller = new AbortController(); caller.abort();
    expect(() => manager.getUpdateState(caller.signal)).toThrow();
    await f.client.dispose(); expect(() => manager.getUpdateState()).toThrow();
});

test('query exposes detached three-way fields before signing and keeps Profile confirmed', async () => {
    const f = await fixture(); const manager = f.client.profileManager;
    const entered = deferred(); const release = deferred(); const sign = f.client.deviceManager.sign.bind(f.client.deviceManager);
    vi.spyOn(f.client.deviceManager, 'sign').mockImplementation(async (...args) => { entered.resolve(); await release.promise; return sign(...args); });
    const original = manager.profile;
    const pending = manager.updateProfile({ nickname: 'pending', bio: null });
    try {
        await entered.promise;
        const state = await manager.getUpdateState();
        expect(state).toEqual({ status: 'publishing', pendingUpdate: { nickname: 'pending', bio: null } });
        expect(manager.profile).toEqual(original);
        (state.pendingUpdate as { nickname: string }).nickname = 'mutated';
        expect((await manager.getUpdateState()).pendingUpdate).toEqual({ nickname: 'pending', bio: null });
    } finally { release.resolve(); }
    expect(await pending).toMatchObject({ nickname: 'pending' });
    expect(manager.profile!.bio).toBeUndefined();
    expect(await manager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test.each([false, true])('republication snapshots stay separate from pending field edits across restart (supplied=%s)', async supplied => {
    const f = await fixture(); const manager = f.client.profileManager;
    const snapshot = supplied ? { ...manager.profile!, nickname: 'snapshot', updatedAt: manager.profile!.updatedAt + 10 } : undefined;
    f.network.failBefore = 'profile.publish';
    await expect(manager.publishProfile(snapshot)).rejects.toBeInstanceOf(TypeError);
    expect(await manager.getUpdateState()).toMatchObject({ status: 'waitingToRetry', pendingUpdate: {} });
    expect(manager.profile!.nickname).toBe('confirmed');
    const request = f.network.requests.filter(value => value.method === 'profile.publish').at(-1)!.body;
    await f.dispose(); f.network.failBefore = undefined;
    const resumed = await f.network.open(91, f.path); const next = resumed.client.profileManager;
    expect((await next.getUpdateState()).pendingUpdate).toEqual({});
    f.network.requests.length = 0;
    expect((await next.publishProfile())!.nickname).toBe(supplied ? 'snapshot' : 'confirmed');
    expect(f.network.requests.find(value => value.method === 'profile.publish')!.body).toEqual(request);
    expect(await next.getUpdateState()).toEqual({ status: 'synchronized' });
});

test('restart and background retry expose diagnostics, notify reentrant readers and isolate observers', async () => {
    const f = await fixture(); const manager = f.client.profileManager;
    f.network.rejectBefore = { method: 'profile.publish', code: 'rate_limited' };
    await expect(manager.updateProfile({ nickname: 'pending', bio: null })).rejects.toBeInstanceOf(RelayError);
    const failed = await manager.getUpdateState();
    expect(failed).toMatchObject({ status: 'waitingToRetry', pendingUpdate: { nickname: 'pending', bio: null }, lastError: { code: 'rate_limited', occurredAt: f.network.clock.wall } });
    expect(failed.nextAttemptAt).toBeGreaterThan(f.network.clock.wall);
    await f.dispose(); const resumed = await f.network.open(91, f.path); const next = resumed.client.profileManager;
    expect(await next.getUpdateState()).toEqual(failed);
    const states: ProfileUpdateState[] = []; const synchronized = deferred(); const errors: unknown[] = [];
    next.onLifecycle('backgroundError', value => { errors.push(value.error); });
    next.on('updateStateChanged', async () => { const state = await next.getUpdateState(); states.push(state); if (state.status === 'synchronized') synchronized.resolve(); });
    next.on('updateStateChanged', () => { throw new Error('sync observer'); });
    next.on('updateStateChanged', async () => { throw new Error('async observer'); });
    f.network.rejectBefore = undefined;
    await next.start(); f.network.clock.tick(); await synchronized.promise;
    expect(states.some(value => value.status === 'publishing')).toBe(true);
    expect(next.profile).toMatchObject({ nickname: 'pending' }); expect(next.profile!.bio).toBeUndefined();
    expect(errors.length).toBeGreaterThan(0); expect(await next.getUpdateState()).toEqual({ status: 'synchronized' });
    await next.stop();
});

test.each([false, true])('failed diagnostic writes retain live error time and retry policy (permanent=%s)', async permanent => {
    const f = await fixture(); const manager = f.client.profileManager;
    f.network.rejectBefore = { method: 'profile.publish', code: permanent ? 'invalid_state' : 'rate_limited' };
    const commit = f.store.commit.bind(f.store);
    const failedWrite = vi.spyOn(f.store, 'commit').mockImplementation((version, mutations, signal) => {
        if (mutations.some(value => value.kind === 'put' && value.key === 'profile.publish' && value.value.lastError != null)) throw new Error('diagnostic write failed');
        return commit(version, mutations, signal);
    });
    await expect(manager.updateProfile({ nickname: 'pending' })).rejects.toBeInstanceOf(AggregateError);
    const state = await manager.getUpdateState();
    expect(state.status).toBe(permanent ? 'failed' : 'waitingToRetry');
    expect(state.lastError).toMatchObject({ code: permanent ? 'invalid_state' : 'rate_limited', occurredAt: f.network.clock.wall });
    expect(state.nextAttemptAt === undefined).toBe(permanent);
    f.network.clock.tick(); expect(await manager.getUpdateState()).toEqual(state);
    failedWrite.mockRestore(); f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(manager.publishProfile()).rejects.toBeInstanceOf(RelayError);
    const saved = await manager.getUpdateState(); expect(saved.lastError!.occurredAt).toBe(f.network.clock.wall);
    await f.dispose(); const resumed = await f.network.open(91, f.path);
    expect(await resumed.client.profileManager.getUpdateState()).toEqual(saved);
    f.network.rejectBefore = undefined;
    await resumed.client.profileManager.updateProfile({ nickname: 'new' });
    expect(await resumed.client.profileManager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test.each([false, true])('late superseded results preserve newer errors, fields and notifications (success=%s)', async success => {
    const f = await fixture(); const manager = f.client.profileManager;
    const connection = f.pool.clients.find(value => value.sessionMode === 'device')!; const request = connection.requestHttp.bind(connection);
    const entered = deferred(); const release = deferred(); let hold = true;
    vi.spyOn(connection, 'requestHttp').mockImplementation(async (...args) => {
        if (args[1] === 'profile.publish' && hold) {
            hold = false; if (success) await request(...args); entered.resolve(); await release.promise;
            if (!success) throw new TypeError('late failure'); return undefined;
        }
        return request(...args);
    });
    const old = manager.updateProfile({ nickname: 'old', bio: 'retained' }); const superseded = expect(old).rejects.toBeInstanceOf(StateUpdateSupersededError);
    try {
        await entered.promise; f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
        await expect(manager.updateProfile({ nickname: 'new' })).rejects.toBeInstanceOf(RelayError); await superseded;
        const state = await manager.getUpdateState(); const confirmed = manager.profile; const notified = vi.fn(); manager.on('updateStateChanged', notified);
        expect(state.pendingUpdate).toEqual({ nickname: 'new', bio: 'retained' });
        release.resolve(); await f.dispose();
        expect(notified).not.toHaveBeenCalled();
        const reopened = await f.network.open(91, f.path); expect(await reopened.client.profileManager.getUpdateState()).toEqual(state);
        expect(reopened.client.profile).toEqual(confirmed);
    } finally { release.resolve(); }
});

test.each(['network', 'invalid_state', 'rate_limited', 'clock_skew'].flatMap(code => ['read', 'write'].map(phase => [code, phase])))('failed outcome persistence retains live diagnostics and retry policy (%s, %s)', async (code, phase) => {
    const f = await fixture(); const manager = f.client.profileManager;
    const connection = f.pool.clients.find(value => value.sessionMode === 'device')!; const request = connection.requestHttp.bind(connection);
    let failStorage = false; let failures = 0; let publications = 0;
    const storageError = Object.assign(new Error('outcome persistence failed'), { code: 'SQLITE_BUSY' });
    vi.spyOn(connection, 'requestHttp').mockImplementation(async (...args) => {
        if (args[1] !== 'profile.publish') return request(...args);
        publications++;
        failStorage = true;
        if (code === 'network') throw new TypeError('publication network failure');
        throw new RelayError({ code, message: 'publication rejected', ...(code === 'rate_limited' ? { data: { retry_after: 90 } } : {}) });
    });
    const commit = f.store.commit.bind(f.store);
    vi.spyOn(f.store, 'commit').mockImplementation((version, mutations, signal) => {
        if (phase === 'write' && failStorage && mutations.some(value => value.kind === 'put' && value.key === publication.key)) {
            failStorage = false; failures++; throw storageError;
        }
        return commit(version, mutations, signal);
    });
    const read = f.store.read.bind(f.store);
    vi.spyOn(f.store, 'read').mockImplementation((queries, signal) => {
        if (phase === 'read' && failStorage && queries.some(query => query.collection === publication.collection && query.key === publication.key)) {
            failStorage = false; failures++; throw storageError;
        }
        return read(queries, signal);
    });
    const failedAt = f.network.clock.wall;
    const work = code === 'clock_skew' ? manager.publishProfile({ ...manager.profile!, updatedAt: failedAt + 120 }) : manager.updateProfile({ nickname: 'pending' });
    const error = await work.catch(error => error as unknown);
    expect(error).toBeInstanceOf(AggregateError);
    expect((error as AggregateError).errors[0]).toBeInstanceOf(code === 'network' ? TypeError : RelayError);
    expect((error as AggregateError).errors[1]).toBe(storageError);
    expect(failures).toBe(1);
    const state = await manager.getUpdateState();
    expect(state.status).toBe(code === 'invalid_state' ? 'failed' : 'waitingToRetry');
    expect(state.lastError).toEqual({ message: code === 'network' ? 'publication network failure' : 'publication rejected', occurredAt: failedAt,
        ...(code === 'network' ? {} : { code }) });
    expect(state.pendingUpdate).toEqual(code === 'clock_skew' ? {} : { nickname: 'pending' });
    expect(state.nextAttemptAt).toBe(code === 'invalid_state' ? undefined : failedAt + (code === 'clock_skew' ? 121 : code === 'rate_limited' ? 90 : 1));
    await manager.start(); await manager.stop(); expect(publications).toBe(1);
    vi.restoreAllMocks();
    await manager.updateProfile({ nickname: 'new' });
    expect(await manager.getUpdateState()).toEqual({ status: 'synchronized' });
    await f.dispose(); const resumed = await f.network.open(91, f.path);
    expect(await resumed.client.profileManager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test.each(['background', 'republication'])('a rejection retained only in memory prevents reuse during %s', async recovery => {
    const f = await fixture(); const manager = f.client.profileManager;
    f.network.rejectBefore = { method: 'profile.publish', code: recovery === 'background' ? 'rate_limited' : 'invalid_state' };
    const commit = f.store.commit.bind(f.store);
    const fault = vi.spyOn(f.store, 'commit').mockImplementation((version, mutations, signal) => {
        if (mutations.some(value => value.kind === 'put' && value.key === publication.key && value.value.requestState === 2)) throw new Error('rejection write failed');
        return commit(version, mutations, signal);
    });
    await expect(manager.updateProfile({ nickname: 'pending' })).rejects.toBeInstanceOf(AggregateError);
    const rejected = f.network.requests.filter(value => value.method === 'profile.publish').at(-1)!.body;
    expect((await f.store.read([publication])).sets[0]![0]!.value.requestState).toBe(1);
    fault.mockRestore(); f.network.rejectBefore = undefined;
    if (recovery === 'background') {
        f.network.clock.tick(); await manager.start();
        await vi.waitFor(async () => expect((await manager.getUpdateState()).status).toBe('synchronized'));
        await manager.stop();
    } else await manager.publishProfile();
    const sent = f.network.requests.filter(value => value.method === 'profile.publish').at(-1)!.body;
    expect(sent!.updated_at).toBeGreaterThan(rejected!.updated_at as number);
    expect(manager.profile!.nickname).toBe('pending');
    await f.dispose(); const resumed = await f.network.open(91, f.path);
    expect(await resumed.client.profileManager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test.each(['invalid_state', 'rate_limited', 'cancellation'])('initial publication retains its actual outcome when cancellation races it (%s)', async outcome => {
    const f = await fixture(false); const manager = f.client.profileManager;
    f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toThrow();
    const caller = new AbortController(); const reason = new Error('caller canceled');
    const connection = f.pool.clients.find(value => value.sessionMode === 'device')!; const request = connection.requestHttp.bind(connection);
    vi.spyOn(connection, 'requestHttp').mockImplementation(async (...args) => {
        if (args[1] !== 'profile.publish') return request(...args);
        caller.abort(reason);
        if (outcome === 'cancellation') throw reason;
        throw new RelayError({ code: outcome, message: 'publication rejected', ...(outcome === 'rate_limited' ? { data: { retry_after: 90 } } : {}) });
    });
    await expect(f.client.establishAccount({}, caller.signal)).rejects.toThrow();
    const state = await manager.getUpdateState();
    expect(state.status).toBe(outcome === 'invalid_state' ? 'failed' : 'waitingToRetry');
    expect(state.lastError).toEqual({ message: outcome === 'cancellation' ? reason.message : 'publication rejected', occurredAt: f.network.clock.wall,
        ...(outcome === 'cancellation' ? {} : { code: outcome }) });
    expect(state.nextAttemptAt).toBe(outcome === 'invalid_state' ? undefined : f.network.clock.wall + (outcome === 'rate_limited' ? 90 : 2));
    await f.dispose(); const resumed = await f.network.open(91, f.path);
    expect(await resumed.client.profileManager.getUpdateState()).toEqual(state);
});

test('caller cancellation retains admitted fields and a later publication clears the error', async () => {
    const f = await fixture(); const manager = f.client.profileManager; const caller = new AbortController();
    const entered = deferred(); const release = deferred(); const sign = f.client.deviceManager.sign.bind(f.client.deviceManager);
    const blocked = vi.spyOn(f.client.deviceManager, 'sign').mockImplementation(async (...args) => { entered.resolve(); await release.promise; return sign(...args); });
    const work = manager.updateProfile({ nickname: 'canceled' }, caller.signal); const canceled = expect(work).rejects.toMatchObject({ name: 'AbortError' });
    try { await entered.promise; caller.abort(); await canceled; } finally { release.resolve(); }
    await vi.waitFor(async () => expect((await manager.getUpdateState()).status).toBe('waitingToRetry'));
    expect((await manager.getUpdateState()).pendingUpdate).toEqual({ nickname: 'canceled' });
    blocked.mockRestore(); await manager.publishProfile(); expect(await manager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test('discovery clears diagnostics and notifies without a new update', async () => {
    const f = await fixture(); f.network.loseAfter = 'profile.publish';
    await expect(f.client.profileManager.updateProfile({ nickname: 'accepted' })).rejects.toBeInstanceOf(TypeError);
    expect((await f.client.profileManager.getUpdateState()).lastError).toBeDefined();
    const notified = vi.fn(); f.client.profileManager.on('updateStateChanged', notified);
    f.network.loseAfter = undefined; await f.client.profileManager.refreshProfile();
    expect(notified).toHaveBeenCalledTimes(1); expect(await f.client.profileManager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test.each(['unprepared', 'unknown', 'rejected'] as const)('older supplied snapshots cannot replace retained intent after %s and restart', async failure => {
    const f = await fixture(); const manager = f.client.profileManager; const cached = manager.profile!;
    const newer = { ...cached, nickname: 'newer', updatedAt: cached.updatedAt + 10 };
    if (failure === 'rejected') f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    else f.network.failBefore = failure === 'unprepared' ? 'profile.resolve' : 'profile.publish';
    await expect(manager.publishProfile(newer)).rejects.toThrow();
    await f.dispose(); const resumed = await f.network.open(91, f.path); const next = resumed.client.profileManager;
    f.network.failBefore = undefined; f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(next.publishProfile(cached)).rejects.toBeInstanceOf(RelayError);
    await resumed.dispose(); const again = await f.network.open(91, f.path); f.network.rejectBefore = undefined;
    expect((await again.client.profileManager.publishProfile())!.nickname).toBe('newer');
    expect(await again.client.profileManager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test.each([false, true])('snapshot merge retains equal versions and accepts newer versions while preserving explicit edits (newer=%s)', async newer => {
    const f = await fixture(); const manager = f.client.profileManager; const cached = manager.profile!;
    const retained = { ...cached, nickname: 'retained', updatedAt: cached.updatedAt + 10 };
    f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(manager.publishProfile(retained)).rejects.toThrow(); await expect(manager.updateProfile({ bio: null })).rejects.toThrow();
    await f.dispose(); const resumed = await f.network.open(91, f.path); f.network.rejectBefore = undefined;
    const result = await resumed.client.profileManager.publishProfile({ ...retained, nickname: 'incoming', updatedAt: retained.updatedAt + Number(newer) });
    expect(result!.nickname).toBe(newer ? 'incoming' : 'retained'); expect(result!.bio).toBeUndefined();
});

test.each([false, true])('initial publication uses public state and preserves creation-only intent through empty retries (publish=%s)', async publish => {
    const f = await fixture(false); f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toBeInstanceOf(AccountEstablishmentError);
    const failed = await f.client.profileManager.getUpdateState();
    expect(failed).toMatchObject({ status: 'failed', pendingUpdate: { nickname: 'initial', publicDiscovery: false }, lastError: { code: 'invalid_state' } });
    const existing = await remote(f); await f.dispose(); f.network.rejectBefore = undefined;
    const resumed = await f.network.open(91, f.path); const next = resumed.client.profileManager; f.network.requests.length = 0;
    expect(await next.getUpdateState()).toEqual(failed);
    const result = publish ? await next.publishProfile() : await next.updateProfile({});
    expect(result).toEqual(existing); expect(f.network.requests.some(value => value.method === 'profile.publish')).toBe(false);
    expect(await next.getUpdateState()).toEqual({ status: 'synchronized' });
    await resumed.client.establishAccount(); expect((await resumed.store.read([progress])).sets[0]).toEqual([]);
});

test.each([false, true])('explicit profile assignments or deletions supersede initial values after restart (delete=%s)', async remove => {
    const f = await fixture(false); f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial', bio: 'initial biography' } })).rejects.toThrow();
    await remote(f); await f.dispose(); f.network.rejectBefore = undefined;
    const resumed = await f.network.open(91, f.path);
    const value = await resumed.client.profileManager.updateProfile({ nickname: remove ? null : 'user' });
    expect(value.nickname).toBe(remove ? undefined : 'user'); expect(value.bio).toBe('initial biography');
    expect(await resumed.client.profileManager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test('background retry confirms an initial profile while account establishment remains explicitly resumable', async () => {
    const f = await fixture(false); f.network.failBefore = 'profile.publish';
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toMatchObject({ reason: 'submissionOutcomeUnknown' });
    await f.dispose(); const resumed = await f.network.open(91, f.path); const manager = resumed.client.profileManager;
    expect((await manager.getUpdateState()).pendingUpdate!.nickname).toBe('initial');
    const done = deferred(); manager.on('updateStateChanged', async () => { if ((await manager.getUpdateState()).status === 'synchronized') done.resolve(); });
    f.network.failBefore = undefined; await manager.start(); f.network.clock.tick(); await done.promise; await manager.stop();
    expect(manager.profile!.nickname).toBe('initial'); expect((await resumed.store.read([progress])).sets[0]).toHaveLength(1);
    await resumed.client.establishAccount(); expect((await resumed.store.read([progress])).sets[0]).toEqual([]);
});

test('discovery between republication read and admission cannot restore retired initial values', async () => {
    const f = await fixture(false); f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toThrow();
    await expect(f.client.profileManager.publishProfile()).rejects.toThrow();
    await remote(f); f.network.rejectBefore = undefined;
    const read = f.store.read.bind(f.store); let discovered = false;
    vi.spyOn(f.store, 'read').mockImplementation(async (...args) => {
        const snapshot = await read(...args);
        if (!discovered && args[0].some(value => value.collection === 'profiles') && args[0].some(value => value.key === 'profile.publish')) {
            discovered = true; await f.client.profileManager.refreshProfile();
            expect(await f.client.profileManager.getUpdateState()).toEqual({ status: 'synchronized' });
        }
        return snapshot;
    });
    const result = await f.client.profileManager.publishProfile();
    expect(discovered).toBe(true); expect(result!.nickname).toBe('existing'); expect(result!.bio).toBe('remote biography');
});

test.each([false, true])('legacy requests distinguish initialization from ordinary edits (initial=%s)', async initial => {
    const f = await fixture(false); f.network.failBefore = 'profile.publish';
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toThrow();
    if (!initial) await expect(f.client.profileManager.updateProfile({ nickname: 'user' })).rejects.toThrow();
    const snapshot = await f.store.read([publication]); const row = snapshot.sets[0]![0]!.value;
    const legacy = { relayId: row.relayId!, revision: row.revision!, document: row.document!, pending: true };
    await f.store.commit(snapshot.version, [{ ...publication, kind: 'put', value: legacy }]);
    await remote(f); await f.dispose(); f.network.failBefore = undefined;
    const resumed = await f.network.open(91, f.path); const next = resumed.client.profileManager;
    const result = await next.publishProfile(); expect(result!.nickname).toBe(initial ? 'existing' : 'user');
    expect(await next.getUpdateState()).toEqual({ status: 'synchronized' });
});

test('initialization adopts ordinary edits admitted while its first discovery is delayed', async () => {
    const f = await fixture(false); f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'default', bio: 'default biography' } })).rejects.toThrow();
    const manager = f.client.profileManager;
    const connection = f.pool.clients.find(value => value.sessionMode === 'device')!; const request = connection.requestHttp.bind(connection);
    const entered = deferred(); const release = deferred(); let hold = true;
    vi.spyOn(connection, 'requestHttp').mockImplementation(async (...args) => {
        if (args[1] === 'profile.resolve' && hold) {
            hold = false;
            try { return await request(...args); } finally { entered.resolve(); await release.promise; }
        }
        return request(...args);
    });
    const setup = f.client.establishAccount();
    try {
        await entered.promise;
        await expect(manager.updateProfile({ nickname: 'user', bio: null })).rejects.toBeInstanceOf(RelayError);
        f.network.rejectBefore = undefined;
    } finally { release.resolve(); }
    await setup;
    expect(manager.profile!.nickname).toBe('user'); expect(manager.profile!.bio).toBeUndefined();
    expect(await manager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test.each([false, true])('discovery retires unprepared initialization and fences its late signing result (userEdit=%s)', async userEdit => {
    const f = await fixture(false); f.network.failBefore = 'profile.publish';
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toThrow();
    f.network.failBefore = undefined;
    // A rejected request requires signing again; initialization still retains creation-only intent.
    f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(f.client.profileManager.publishProfile()).rejects.toThrow();
    f.network.rejectBefore = undefined;
    const manager = f.client.profileManager; const entered = deferred(); const release = deferred();
    const sign = f.client.deviceManager.sign.bind(f.client.deviceManager); let hold = true;
    const blocked = vi.spyOn(f.client.deviceManager, 'sign').mockImplementation(async (...args) => {
        if (hold) { hold = false; entered.resolve(); await release.promise; }
        return sign(...args);
    });
    const old = manager.publishProfile(); const superseded = expect(old).rejects.toBeInstanceOf(StateUpdateSupersededError);
    try {
        await entered.promise;
        if (userEdit) {
            f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
            await expect(manager.updateProfile({ nickname: 'user' })).rejects.toThrow();
            f.network.rejectBefore = undefined;
        }
        await remote(f); await manager.refreshProfile();
        const state = await manager.getUpdateState();
        if (userEdit) expect(state.pendingUpdate!.nickname).toBe('user');
        else expect(state).toEqual({ status: 'synchronized' });
        release.resolve(); await superseded; blocked.mockRestore(); await f.dispose();
        const resumed = await f.network.open(91, f.path);
        expect(await resumed.client.profileManager.getUpdateState()).toEqual(state);
        expect(resumed.client.profile!.nickname).toBe('existing');
    } finally { release.resolve(); }
});

test('failed admission and invalid inputs preserve the prior fields and diagnostics', async () => {
    const f = await fixture(); const manager = f.client.profileManager;
    f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(manager.updateProfile({ nickname: 'retained' })).rejects.toThrow();
    const original = await manager.getUpdateState();
    await expect(manager.updateProfile({ publicDiscovery: null } as never)).rejects.toThrow();
    expect(await manager.getUpdateState()).toEqual(original);
    const failed = vi.spyOn(f.store, 'commit').mockRejectedValueOnce(new Error('admission failed'));
    await expect(manager.updateProfile({ nickname: 'lost' })).rejects.toThrow('admission failed'); failed.mockRestore();
    expect(await manager.getUpdateState()).toEqual(original);
    await f.dispose(); const resumed = await f.network.open(91, f.path);
    expect(await resumed.client.profileManager.getUpdateState()).toEqual(original);
    f.network.rejectBefore = undefined; expect((await resumed.client.profileManager.publishProfile())!.nickname).toBe('retained');
});

test('relay retry hints and original failure time survive reopening', async () => {
    const f = await fixture(); const manager = f.client.profileManager;
    const connection = f.pool.clients.find(value => value.sessionMode === 'device')!; const request = connection.requestHttp.bind(connection);
    vi.spyOn(connection, 'requestHttp').mockImplementation((...args) => {
        if (args[1] === 'profile.publish') throw new RelayError({ code: 'rate_limited', message: 'Later', data: { retry_after: 125 } }, 429);
        return request(...args);
    });
    await expect(manager.updateProfile({ nickname: 'pending' })).rejects.toBeInstanceOf(RelayError);
    const failed = await manager.getUpdateState();
    expect(failed).toMatchObject({ lastError: { code: 'rate_limited', message: 'Later', occurredAt: f.network.clock.wall }, nextAttemptAt: f.network.clock.wall + 125 });
    await f.dispose(); const resumed = await f.network.open(91, f.path);
    expect(await resumed.client.profileManager.getUpdateState()).toEqual(failed);
});

test('unknown initial outcome keeps its classification when saving diagnostics also fails', async () => {
    const f = await fixture(false); const commit = f.store.commit.bind(f.store);
    vi.spyOn(f.store, 'commit').mockImplementation((version, mutations, signal) => {
        if (mutations.some(value => value.kind === 'put' && value.key === 'profile.publish' && value.value.lastError != null)) throw new Error('diagnostics failed');
        return commit(version, mutations, signal);
    });
    f.network.failBefore = 'profile.publish';
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toMatchObject({ reason: 'submissionOutcomeUnknown' });
    const state = await f.client.profileManager.getUpdateState();
    expect(state.status).toBe('waitingToRetry'); expect(state.lastError!.occurredAt).toBe(f.network.clock.wall);
    expect(state.pendingUpdate!.nickname).toBe('initial');
});

test('superseded initialization releases the account operation before its old HTTP result returns', async () => {
    const f = await fixture(false); const manager = f.client.profileManager;
    f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toThrow();
    f.network.rejectBefore = undefined;
    const connection = f.pool.clients.find(value => value.sessionMode === 'device')!;
    const request = connection.requestHttp.bind(connection); const entered = deferred(); const release = deferred(); let first = true;
    vi.spyOn(connection, 'requestHttp').mockImplementation(async (...args) => {
        if (args[1] === 'profile.publish' && first) { first = false; entered.resolve(); await release.promise; }
        return request(...args);
    });
    let outcome: unknown;
    const setup = f.client.establishAccount().catch(error => { outcome = error; });
    try {
        await entered.promise;
        expect((await manager.updateProfile({ nickname: 'new' })).nickname).toBe('new');
        await vi.waitFor(() => expect(outcome).toBeInstanceOf(StateUpdateSupersededError));
        await setup;
        await f.client.establishAccount();
        expect(await manager.getUpdateState()).toEqual({ status: 'synchronized' });
    } finally { release.resolve(); await setup; }
    await f.dispose(); const resumed = await f.network.open(91, f.path);
    expect(resumed.client.profile!.nickname).toBe('new');
    expect(await resumed.client.profileManager.getUpdateState()).toEqual({ status: 'synchronized' });
});

test.each(['update', 'republication', 'background'] as const)('initialization joining %s emits one confirmed profile change', async owner => {
    const f = await fixture(false); const manager = f.client.profileManager;
    f.network.rejectBefore = { method: 'profile.publish', code: 'invalid_state' };
    await expect(f.client.establishAccount({ initialProfile: { nickname: 'initial' } })).rejects.toThrow();
    f.network.rejectBefore = undefined; f.network.failBefore = 'profile.publish';
    await expect(manager.updateProfile({ nickname: 'user' })).rejects.toThrow();
    f.network.failBefore = undefined; f.network.requests.length = 0;
    const connection = f.pool.clients.find(value => value.sessionMode === 'device')!;
    const request = connection.requestHttp.bind(connection); const entered = deferred(); const release = deferred();
    vi.spyOn(connection, 'requestHttp').mockImplementation(async (...args) => {
        if (args[1] === 'profile.publish') { entered.resolve(); await release.promise; }
        return request(...args);
    });
    const changed = vi.fn(); manager.on('profileChanged', changed);
    let edit: Promise<AccountProfile | undefined> | undefined;
    if (owner === 'background') { f.network.clock.tick(); await manager.start(); }
    else edit = owner === 'update' ? manager.updateProfile({ nickname: 'user' }) : manager.publishProfile();
    let setup: Promise<void> | undefined;
    try {
        await entered.promise;
        const read = f.store.read.bind(f.store); const joined = deferred();
        vi.spyOn(f.store, 'read').mockImplementation(async (...args) => {
            const result = await read(...args);
            if (args[0].length === 1 && args[0][0]!.key === 'profile.publish') joined.resolve();
            return result;
        });
        setup = f.client.establishAccount();
        await joined.promise;
        release.resolve(); await edit; await setup;
        expect(manager.profile!.nickname).toBe('user');
        expect(f.network.requests.filter(value => value.method === 'profile.publish')).toHaveLength(1);
        expect(changed).toHaveBeenCalledTimes(1);
        expect(await manager.getUpdateState()).toEqual({ status: 'synchronized' });
    } finally { release.resolve(); await Promise.allSettled([edit, setup]); await manager.stop(); }
});

test('a confirmed profile observer can dispose the client without changing the publication result', async () => {
    const f = await fixture(); const manager = f.client.profileManager;
    let disposal: Promise<void> | undefined;
    manager.on('profileChanged', () => { disposal = f.client.dispose(); return disposal; });
    const result = await manager.updateProfile({ nickname: 'confirmed before disposal' });
    expect(result.nickname).toBe('confirmed before disposal');
    await vi.waitFor(() => expect(disposal).toBeDefined());
    await disposal;
    expect(f.client.lifecycleState).toBe('disposed');
});
