import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test, vi } from 'vitest';
import {
    AccountManager, RelayClientPool, accountPublicKey, getAccountId, accountRouteCodec, canonicalJson, parseJson, requireObject,
    relayDescriptorCodec, routeAccountInput, routeRelayInput, signAccount, type AccountRoute, type AccountSigner, type JsonObject, type RelayFetch,
} from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { context, relayPrivateKey, signedDescriptor, TestRegistry } from '../support/relay-fixture.js';
import { AdvancingClock } from '../support/clock.js';
import { removeTestDirectory } from '../support/temp.js';

const accountKey = new Uint8Array(32).fill(3);
const publicKey = accountPublicKey(accountKey);
const accountId = getAccountId('neo:860833102', publicKey);
const resources: { dispose(): Promise<void> }[] = []; const directories: string[] = [];
afterEach(async () => { for (const resource of resources.splice(0).reverse()) await resource.dispose(); for (const path of directories.splice(0)) await removeTestDirectory(path); });
function response(body: unknown, status = 200): Response { return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }); }

async function fixture(path?: string) {
    if (!path) { const directory = await mkdtemp(join(tmpdir(), 'meshline-account-')); directories.push(directory); path = join(directory, 'state.sqlite'); }
    const store = new NodeSqliteStore(path); resources.push(store); await store.migrate();
    const registry = new TestRegistry(); const descriptor = signedDescriptor(); const clock = new AdvancingClock();
    const requests: string[] = []; let routeSigns = 0; let route: AccountRoute | undefined;
    let behavior: 'ok' | 'lost' | 'reject' | 'tamper' | 'forged' | 'malformed' = 'ok';
    let signHook: (() => Promise<void>) | undefined;
    const signer: AccountSigner = { accountId, publicKey, async sign(input) {
        if (requireObject(parseJson(input))['$type'] === 'meshline.account.route') { routeSigns++; await signHook?.(); }
        return signAccount(input, accountKey);
    } };
    const fetch: RelayFetch = async (url, init) => {
        const name = new URL(url).pathname.replace(/^\/v1/, '').slice(1).replaceAll('/', '.');
        if (name === 'relay.descriptor') return response(relayDescriptorCodec.encode(descriptor));
        if (name === 'auth.challenge') return response({ nonce: 'challenge', created_at: clock.wall, expires_at: clock.wall + 300 });
        if (name === 'auth.account.verify') return response({ token: 'account-session', mode: 'account', expires_at: clock.wall + 3600 });
        if (name === 'account.route.resolve') return route ? response(accountRouteCodec.encode(route)) : response({ code: 'not_found', message: 'Unknown account' }, 404);
        if (name !== 'account.route.publish') throw new Error(`Unexpected method ${name}`);
        expect(init.method).toBe('PUT'); expect(init.headers['X-Meshline-Session']).toBe('account-session');
        const pending = (await store.read([{ collection: 'signed_requests', key: name }])).sets[0]![0]!.value;
        expect(pending.pending).toBe(true); expect(canonicalJson(pending.document!)).toBe(init.body);
        requests.push(init.body!);
        if (behavior === 'reject') return response({ code: 'forbidden', message: 'Rejected' }, 403);
        if (behavior === 'malformed') return response({ nope: true });
        const request = accountRouteCodec.parse(init.body!);
        route = { ...request, relaySignature: signAccount(routeRelayInput(request, context), relayPrivateKey) };
        if (behavior === 'lost') throw new TypeError('Response lost after server accepted the route');
        if (behavior === 'tamper') return response(accountRouteCodec.encode({ ...route, expiresAt: route.expiresAt + 1 }));
        if (behavior === 'forged') return response(accountRouteCodec.encode({ ...route, relaySignature: new Uint8Array(64) }));
        return response(accountRouteCodec.encode(route));
    };
    const pool = new RelayClientPool({ context, accountId, fetch, clock }, registry); resources.push(pool);
    const manager = new AccountManager({ context, accountId, store, relayClients: pool, accountSigner: signer, clock }); resources.push(manager);
    const makeRoute = (revision: number, extra: Partial<AccountRoute> = {}): AccountRoute => {
        let value: AccountRoute = { account: accountId, accountPublicKey: publicKey, revision, relayId: descriptor.relayId,
            updatedAt: clock.wall, expiresAt: clock.wall + 3600, accountSignature: new Uint8Array(64), ...extra };
        value = { ...value, accountSignature: signAccount(routeAccountInput(value, context), accountKey) };
        return { ...value, relaySignature: signAccount(routeRelayInput(value, context), relayPrivateKey) };
    };
    return { store, pool, manager, clock, registry, descriptor, requests, makeRoute,
        set behavior(value: typeof behavior) { behavior = value; }, set route(value: AccountRoute | undefined) { route = value; },
        set signHook(value: (() => Promise<void>) | undefined) { signHook = value; },
        get routeSigns() { return routeSigns; }, get route() { return route; },
        async pending() { return (await store.read([{ collection: 'signed_requests', key: 'account.route.publish' }])).sets[0]![0]?.value; },
    };
}

test('initialization loads storage explicitly and publication atomically saves route and acknowledgement', async () => {
    const f = await fixture(); expect(f.manager.lifecycleState).toBe('uninitialized'); expect(f.requests).toEqual([]);
    await f.manager.initialize(); expect(f.manager.state).toBe('unknown'); expect(await f.manager.getRoute()).toBeUndefined();
    let events = 0; f.manager.on('accountChanged', () => { events++; });
    const route = await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 });
    expect(route.revision).toBe(0); expect(route.relaySignature).toHaveLength(64); expect(events).toBe(1);
    const saved = await f.store.read([{ collection: 'account_routes', key: accountId }, { collection: 'signed_requests', key: 'account.route.publish' }]);
    expect(saved.sets[0]![0]!.revision).toBe(saved.sets[1]![0]!.revision); expect(saved.sets[1]![0]!.value.pending).toBe(false);
    route.accountSignature.fill(0); expect(f.manager.route!.accountSignature).not.toEqual(route.accountSignature);
    f.manager.route!.accountPublicKey.fill(0); expect(f.manager.route!.accountPublicKey).toEqual(publicKey);
});

test('unknown publication outcome survives restart and explicit retry sends the exact original bytes', async () => {
    const first = await fixture(); await first.manager.initialize(); first.behavior = 'lost';
    await expect(first.manager.publishRoute(first.descriptor.relayId, { validitySeconds: 3600 })).rejects.toThrow('Response lost');
    expect((await first.pending())!.pending).toBe(true); expect(first.requests).toHaveLength(1);
    await first.manager.dispose(); await first.pool.dispose(); await first.store.dispose();
    const resumed = await fixture(first.store.path); await resumed.manager.initialize(); resumed.clock.wall += 25;
    await expect(resumed.manager.publishRoute(resumed.descriptor.relayId, { validitySeconds: 7200 })).rejects.toThrow('unknown result');
    expect(resumed.requests).toEqual([]);
    await resumed.manager.publishRoute(resumed.descriptor.relayId, { validitySeconds: 3600 });
    expect(resumed.requests).toEqual(first.requests); expect(resumed.routeSigns).toBe(0); expect((await resumed.pending())!.pending).toBe(false);
});

test('resolving an accepted route settles a lost acknowledgement and later not_found retains the known route', async () => {
    const f = await fixture(); await f.manager.initialize(); f.behavior = 'lost';
    await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 })).rejects.toThrow();
    const route = await f.manager.getRoute(); expect(route).toEqual(f.route); expect((await f.pending())!.pending).toBe(false);
    f.route = undefined; expect(await f.manager.getRoute()).toBeUndefined(); expect(f.manager.state).toBe('unknown'); expect(f.manager.route).toEqual(route);
});

test('definitive rejection retires the pending request and the next publication advances revision', async () => {
    const f = await fixture(); await f.manager.initialize(); f.behavior = 'reject';
    await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 })).rejects.toMatchObject({ code: 'forbidden' });
    expect((await f.pending())!.pending).toBe(false); f.behavior = 'ok';
    expect((await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 7200 })).revision).toBe(1);
    expect(f.routeSigns).toBe(2);
});

test.each(['tamper', 'forged', 'malformed'] as const)('%s acknowledgement cannot clear durable pending state', async behavior => {
    const f = await fixture(); await f.manager.initialize(); f.behavior = behavior;
    await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 })).rejects.toThrow();
    expect((await f.pending())!.pending).toBe(true); expect(f.manager.route).toBeUndefined(); expect(f.requests).toHaveLength(1);
});

test('discovery rejects forged account/relay proofs and stale or conflicting revisions without erasing cached state', async () => {
    const f = await fixture(); await f.manager.initialize(); f.route = f.makeRoute(10);
    const current = await f.manager.getRoute();
    for (const candidate of [f.makeRoute(9), f.makeRoute(10, { expiresAt: f.clock.wall + 7200 }),
        { ...f.makeRoute(11), accountSignature: new Uint8Array(64) }, { ...f.makeRoute(11), relaySignature: new Uint8Array(64) }]) {
        f.route = candidate; await expect(f.manager.getRoute()).rejects.toThrow(); expect(f.manager.route).toEqual(current);
    }
});

test('expired pending requests and explicit recovery create higher revisions, and overflow never signs', async () => {
    const f = await fixture(); await f.manager.initialize(); f.behavior = 'lost';
    await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 1 })).rejects.toThrow();
    f.clock.wall++; f.behavior = 'ok';
    expect((await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 })).revision).toBe(1);
    expect((await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600, recovery: true })).revision).toBe(f.clock.wall * 1000);
    await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600, revision: Number.MAX_SAFE_INTEGER });
    const before = f.routeSigns;
    await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 })).rejects.toThrow(); expect(f.routeSigns).toBe(before);
});

test('concurrent unrelated store writes do not repeat signing, while conflicting route writes prevent publication', async () => {
    const f = await fixture(); await f.manager.initialize();
    f.signHook = async () => { await f.store.commit((await f.store.read([])).version, [{ collection: 'other', key: 'cursor', kind: 'put', value: { sequence: 1 } }]); };
    await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 }); expect(f.routeSigns).toBe(1);
    f.signHook = async () => { await f.store.commit((await f.store.read([])).version, [{ collection: 'account_routes', key: accountId, kind: 'put', value: accountRouteCodec.encode(f.makeRoute(100)) }]); };
    await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 })).rejects.toThrow('changed while'); expect(f.requests).toHaveLength(1);
});

test('account change observers may dispose the manager without deadlocking or owning the shared pool/store', async () => {
    const f = await fixture(); await f.manager.initialize(); f.manager.on('accountChanged', () => f.manager.dispose());
    await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 }); await vi.waitFor(() => expect(f.manager.lifecycleState).toBe('disposed'));
    expect((await f.store.read([])).version).toBeGreaterThan(0); expect(f.pool.clients.some(client => !client.isDisposed)).toBe(true);
});

test('a pending route observer cannot conceal other observer failures or change a committed publication', async () => {
    const f = await fixture(); await f.manager.initialize(); let release!: () => void;
    const pending = new Promise<void>(resolve => { release = resolve; }); const failures: unknown[] = []; const observed: number[] = [];
    f.manager.on('accountChanged', () => pending);
    f.manager.on('accountChanged', () => { throw new Error('sync observer'); });
    f.manager.on('accountChanged', async () => { await Promise.resolve(); throw new Error('async observer'); });
    f.manager.on('accountChanged', value => { observed.push(value.route!.revision); });
    f.manager.onLifecycle('backgroundError', failure => { failures.push(failure.error); });
    try {
        const route = await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 });
        expect(observed).toEqual([route.revision]); expect((await f.pending())!.pending).toBe(false);
        await vi.waitFor(() => expect(failures).toHaveLength(2));
        expect(failures).toEqual([
            expect.objectContaining({ errors: [expect.objectContaining({ message: 'sync observer' })] }),
            expect.objectContaining({ errors: [expect.objectContaining({ message: 'async observer' })] }),
        ]);
        expect(f.manager.lastBackgroundError).toMatchObject({ operation: 'observer', resource: 'accountChanged', error: failures[1] });
        await f.manager.dispose(); expect(f.manager.lifecycleState).toBe('disposed');
    } finally { release(); }
});
