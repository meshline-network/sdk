import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test, vi } from 'vitest';
import {
    AccountManager, NetworkContext, RelayClientPool, accountPublicKey, getAccountId, accountRouteCodec, canonicalJson, parseJson, requireObject,
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
function deferred() { let resolve!: () => void; const promise = new Promise<void>(done => { resolve = done; }); return { promise, resolve }; }

async function fixture(path?: string) {
    if (!path) { const directory = await mkdtemp(join(tmpdir(), 'meshline-account-')); directories.push(directory); path = join(directory, 'state.sqlite'); }
    const store = new NodeSqliteStore(path); resources.push(store); await store.migrate();
    const registry = new TestRegistry(); const descriptor = signedDescriptor(); const clock = new AdvancingClock();
    const requests: string[] = []; let routeSigns = 0; let route: AccountRoute | undefined;
    let lookups = 0; let resolveHook: ((account: string, signal: AbortSignal) => Promise<AccountRoute | undefined>) | undefined;
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
        if (name === 'account.route.resolve') {
            lookups++;
            const resolved = resolveHook ? await resolveHook(new URL(url).searchParams.get('account')!, init.signal) : route;
            return resolved ? response(accountRouteCodec.encode(resolved)) : response({ code: 'not_found', message: 'Unknown account' }, 404);
        }
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
    const makeRoute = (revision: number, extra: Partial<AccountRoute> = {}, key = accountKey): AccountRoute => {
        const routePublicKey = accountPublicKey(key);
        let value: AccountRoute = { account: getAccountId('neo:860833102', routePublicKey), accountPublicKey: routePublicKey, revision, relayId: descriptor.relayId,
            updatedAt: clock.wall, expiresAt: clock.wall + 3600, accountSignature: new Uint8Array(64), ...extra };
        value = { ...value, accountSignature: signAccount(routeAccountInput(value, context), key) };
        return { ...value, relaySignature: signAccount(routeRelayInput(value, context), relayPrivateKey) };
    };
    return { store, pool, manager, clock, registry, descriptor, requests, makeRoute,
        set behavior(value: typeof behavior) { behavior = value; }, set route(value: AccountRoute | undefined) { route = value; },
        set signHook(value: (() => Promise<void>) | undefined) { signHook = value; },
        set resolveHook(value: typeof resolveHook) { resolveHook = value; }, get lookups() { return lookups; },
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
    const reads = f.lookups; const cached = await f.manager.getRoute(); cached!.accountPublicKey.fill(0);
    expect((await f.manager.getRoute())!.accountPublicKey).toEqual(publicKey); expect(f.lookups).toBe(reads); expect(events).toBe(1);
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
    f.route = undefined; expect(await f.manager.refreshRoute()).toBeUndefined(); expect(f.manager.state).toBe('established');
    expect(await f.manager.getRoute()).toEqual(route); expect(f.manager.route).toEqual(route);
});

test('definitive rejection retires the pending request and the next publication advances revision', async () => {
    const f = await fixture(); await f.manager.initialize(); f.behavior = 'reject';
    await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 })).rejects.toMatchObject({ code: 'forbidden' });
    expect((await f.pending())!.pending).toBe(false); f.behavior = 'ok';
    expect((await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 7200 })).revision).toBe(1);
    expect(f.routeSigns).toBe(2);
});

test('ensureRoute reuses a confirmed unexpired route without discovery or signing', async () => {
    const f = await fixture(); await f.manager.initialize();
    const route = await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 });
    const lookups = f.lookups; const signatures = f.routeSigns;
    expect(await f.manager.ensureRoute(f.descriptor.relayId, 7200)).toEqual(route);
    expect(f.lookups).toBe(lookups); expect(f.routeSigns).toBe(signatures);
});

test.each([true, false])('ensureRoute reconciles an unknown successor while an older route is valid (accepted=%s)', async accepted => {
    const f = await fixture(); await f.manager.initialize();
    const old = await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 });
    f.clock.wall++; f.behavior = 'lost';
    await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 3600 })).rejects.toThrow('Response lost');
    const request = f.requests[1]; const signatures = f.routeSigns;
    if (!accepted) f.route = old;
    f.behavior = 'ok';
    const result = await f.manager.ensureRoute(f.descriptor.relayId, 3600);
    expect(result.revision).toBe(1); expect(f.routeSigns).toBe(signatures);
    expect(f.requests).toHaveLength(accepted ? 2 : 3);
    if (!accepted) expect(f.requests[2]).toBe(request);
    expect((await f.pending())!.pending).toBe(false);
});

test('ensureRoute preserves an expired initial request without original-route evidence', async () => {
    const f = await fixture(); await f.manager.initialize(); f.behavior = 'lost';
    await expect(f.manager.ensureRoute(f.descriptor.relayId, 1)).rejects.toMatchObject({ reason: 'submissionOutcomeUnknown' });
    const pending = await f.pending(); f.route = undefined; f.clock.wall += 2;
    await expect(f.manager.ensureRoute(f.descriptor.relayId, 1)).rejects.toMatchObject({ reason: 'stateConflict' });
    expect(await f.pending()).toEqual(pending); expect(f.requests).toHaveLength(1);
});

test('ensureRoute renews above an expired unknown successor using the retained original route', async () => {
    const f = await fixture(); await f.manager.initialize();
    const original = await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 1 });
    f.behavior = 'lost'; await expect(f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 1 })).rejects.toThrow('Response lost');
    const previous = accountRouteCodec.decode((await f.pending())!.document!);
    f.clock.wall += 2; f.route = undefined; f.behavior = 'ok';
    const renewed = await f.manager.ensureRoute(f.descriptor.relayId, 1);
    expect(renewed.revision).toBe(previous.revision + 1); expect(renewed.relayId).toBe(original.relayId);
    expect((await f.pending())!.pending).toBe(false);
});

test('ensureRoute discovery does not hold the publication gate or overwrite a concurrently confirmed route', async () => {
    const f = await fixture(); await f.manager.initialize(); await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 1 });
    f.clock.wall += 2; const entered = deferred(); const release = deferred();
    f.resolveHook = async () => { entered.resolve(); await release.promise; return undefined; };
    const ensuring = f.manager.ensureRoute(f.descriptor.relayId, 3600); await entered.promise;
    let published: AccountRoute;
    try { published = await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 7200 }); }
    finally { release.resolve(); }
    expect(await ensuring).toEqual(published!); expect(f.requests).toHaveLength(2);
});

test('ensureRoute retries a definitively rejected initial request exactly after corrected preconditions', async () => {
    const f = await fixture(); await f.manager.initialize(); f.behavior = 'reject';
    await expect(f.manager.ensureRoute(f.descriptor.relayId, 3600)).rejects.toMatchObject({ reason: 'stateUnavailable' });
    const request = f.requests[0]; const signatures = f.routeSigns;
    f.behavior = 'ok'; expect((await f.manager.ensureRoute(f.descriptor.relayId, 3600)).revision).toBe(0);
    expect(f.requests).toEqual([request, request]); expect(f.routeSigns).toBe(signatures);
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
        f.route = candidate; await expect(f.manager.refreshRoute()).rejects.toThrow(); expect(f.manager.route).toEqual(current);
        expect(await f.manager.getRoute()).toEqual(current);
    }
});

test('cold callers share discovery and receive independent route copies', async () => {
    const f = await fixture(); await f.manager.initialize(); const route = f.makeRoute(1);
    const entered = deferred(); const release = deferred();
    f.resolveHook = async () => { entered.resolve(); await release.promise; return route; };
    const callers = Array.from({ length: 8 }, () => f.manager.getRoute());
    try { await entered.promise; expect(f.lookups).toBe(1); }
    finally { release.resolve(); }
    const values = await Promise.all(callers); expect(values).toEqual(Array(8).fill(route)); expect(f.lookups).toBe(1);
    values[0]!.accountSignature.fill(0); expect(values[1]).toEqual(route); expect(await f.manager.getRoute()).toEqual(route);
});

test('different accounts resolve independently even while one discovery is blocked', async () => {
    const f = await fixture(); await f.manager.initialize(); const own = f.makeRoute(1); const peer = f.makeRoute(2, {}, new Uint8Array(32).fill(4));
    const entered = deferred(); const release = deferred();
    f.resolveHook = async account => { if (account !== accountId) return peer; entered.resolve(); await release.promise; return own; };
    const blocked = f.manager.getRoute(); await entered.promise;
    let other: AccountRoute | undefined; const parallel = f.manager.getRoute(peer.account).then(value => { other = value; });
    try { await vi.waitFor(() => expect(other).toEqual(peer)); expect(f.lookups).toBe(2); }
    finally { release.resolve(); await Promise.all([blocked, parallel]); }
    expect(await f.manager.getRoute()).toEqual(own); expect(await f.manager.getRoute(peer.account)).toEqual(peer); expect(f.lookups).toBe(2);
});

test.each([false, true])('one-hour freshness yields a nonblocking shared refresh; changed=%s', async newer => {
    const f = await fixture(); await f.manager.initialize(); f.route = f.makeRoute(1, { expiresAt: f.clock.wall + 7200 });
    const old = await f.manager.getRoute(); let events = 0; f.manager.on('accountChanged', () => { events++; });
    f.clock.elapsed += 3_599_999;
    expect(await f.manager.getRoute()).toEqual(old); expect(f.lookups).toBe(1);
    const next = newer ? f.makeRoute(2, { expiresAt: f.clock.wall + 7200 }) : old;
    const entered = deferred(); const release = deferred();
    f.resolveHook = async () => { entered.resolve(); await release.promise; return next; }; f.clock.elapsed++;
    let visible: AccountRoute | undefined; const read = f.manager.getRoute().then(route => { visible = route; });
    await entered.promise; const refreshed = f.manager.refreshRoute();
    try {
        await vi.waitFor(() => expect(visible).toEqual(old));
        expect(await f.manager.getRoute()).toEqual(old); expect(f.lookups).toBe(2);
    } finally { release.resolve(); await read; }
    expect(await refreshed).toEqual(next); expect(f.manager.route).toEqual(next); expect(f.lookups).toBe(2);
    expect(events).toBe(newer ? 1 : 0);
});

test.each(['not_found', 'failure'] as const)('%s refresh retains a usable route and defers ordinary retries for one minute', async outcome => {
    const f = await fixture(); await f.manager.initialize(); f.route = f.makeRoute(1, { expiresAt: f.clock.wall + 7200 }); const old = await f.manager.getRoute();
    const failures: unknown[] = []; f.manager.onLifecycle('backgroundError', value => { failures.push(value.error); });
    f.resolveHook = async () => { if (outcome === 'failure') throw new TypeError('offline'); return undefined; };
    const refreshed = f.manager.refreshRoute();
    if (outcome === 'failure') await expect(refreshed).rejects.toThrow(); else expect(await refreshed).toBeUndefined();
    expect(failures).toHaveLength(outcome === 'failure' ? 1 : 0);
    // Freshness has elapsed, but the failure cooldown still applies.
    f.clock.elapsed = 3_600_000;
    const again = f.manager.refreshRoute();
    if (outcome === 'failure') await expect(again).rejects.toThrow(); else await again;
    const lookups = f.lookups; f.clock.elapsed += 59_999;
    expect(await f.manager.getRoute()).toEqual(old); expect(f.lookups).toBe(lookups); expect(f.manager.state).toBe('established');
    const entered = deferred(); const release = deferred(); f.resolveHook = async () => { entered.resolve(); await release.promise; return old; }; f.clock.elapsed++;
    try { expect(await f.manager.getRoute()).toEqual(old); await entered.promise; expect(f.lookups).toBe(lookups + 1); }
    finally { release.resolve(); }
});

test.each(['not_found', 'failure'] as const)('cold %s is reused during cooldown; forced discovery bypasses it', async outcome => {
    const f = await fixture(); await f.manager.initialize(); f.resolveHook = async () => { if (outcome === 'failure') throw new TypeError('offline'); return undefined; };
    for (let attempt = 0; attempt < 2; attempt++) {
        const read = f.manager.getRoute();
        if (outcome === 'failure') await expect(read).rejects.toThrow(); else expect(await read).toBeUndefined();
    }
    expect(f.lookups).toBe(1); f.clock.elapsed += 60_000;
    const retry = f.manager.getRoute();
    if (outcome === 'failure') await expect(retry).rejects.toThrow(); else await retry;
    expect(f.lookups).toBe(2);
    f.route = f.makeRoute(1); f.resolveHook = undefined;
    expect(await f.manager.refreshRoute()).toEqual(f.route); expect(f.lookups).toBe(3);
    expect(await f.manager.getRoute()).toEqual(f.route); expect(f.lookups).toBe(3);
});

test('signed expiration is a hard limit even inside freshness and failure cooldown', async () => {
    const f = await fixture(); await f.manager.initialize(); f.route = f.makeRoute(1, { expiresAt: f.clock.wall + 10 }); await f.manager.getRoute();
    f.resolveHook = async () => { throw new TypeError('offline'); }; await expect(f.manager.refreshRoute()).rejects.toThrow();
    f.clock.wall += 10; await expect(f.manager.getRoute()).rejects.toThrow(); expect(f.lookups).toBe(2);
    f.clock.elapsed += 60_000; f.resolveHook = undefined; f.route = f.makeRoute(2);
    expect(await f.manager.getRoute()).toEqual(f.route); expect(f.lookups).toBe(3);
});

test('canceling a cold caller leaves shared discovery alive for other callers', async () => {
    const f = await fixture(); await f.manager.initialize(); const route = f.makeRoute(1);
    const entered = deferred(); const release = deferred(); let transport!: AbortSignal;
    f.resolveHook = async (_account, signal) => { transport = signal; entered.resolve(); await release.promise; return route; };
    const caller = new AbortController(); const canceled = expect(f.manager.getRoute(undefined, caller.signal)).rejects.toMatchObject({ name: 'AbortError' });
    await entered.promise; const other = f.manager.getRoute();
    try { caller.abort(); await canceled; expect(transport.aborted).toBe(false); expect(f.lookups).toBe(1); }
    finally { release.resolve(); }
    expect(await other).toEqual(route); expect(await f.manager.getRoute()).toEqual(route); expect(f.lookups).toBe(1);
});

test('a refresh abandoned by every caller still reports failure and applies cooldown', async () => {
    const f = await fixture(); await f.manager.initialize(); const entered = deferred(); const release = deferred(); const failed = deferred();
    f.manager.onLifecycle('backgroundError', () => { failed.resolve(); });
    f.resolveHook = async () => { entered.resolve(); await release.promise; throw new TypeError('offline'); };
    const caller = new AbortController(); const canceled = expect(f.manager.getRoute(undefined, caller.signal)).rejects.toMatchObject({ name: 'AbortError' });
    try { await entered.promise; caller.abort(); await canceled; } finally { release.resolve(); }
    await failed.promise; await expect(f.manager.getRoute()).rejects.toThrow(); expect(f.lookups).toBe(1);
});

test.each([
    ['stop', false], ['stop', true], ['dispose', false], ['dispose', true],
] as const)('%s drains discovery with running=%s, including background work without waiters', async (action, running) => {
    const f = await fixture(); await f.manager.initialize(); f.route = f.makeRoute(1, { expiresAt: f.clock.wall + 7200 });
    if (running) { await f.manager.getRoute(); await f.manager.start(); f.clock.elapsed += 3_600_000; }
    else { f.resolveHook = async () => { throw new TypeError('offline'); }; await expect(f.manager.getRoute()).rejects.toThrow(); }
    const failures: unknown[] = []; f.manager.onLifecycle('backgroundError', value => { failures.push(value.error); });
    const entered = deferred(); const canceled = deferred(); const release = deferred(); let completed = false;
    f.resolveHook = async (_account, signal) => {
        entered.resolve();
        await new Promise<void>(resolve => { signal.addEventListener('abort', () => { canceled.resolve(); resolve(); }, { once: true }); });
        await release.promise; completed = true; throw signal.reason;
    };
    const read = running ? f.manager.getRoute() : f.manager.refreshRoute(); const observed = running ? read : expect(read).rejects.toMatchObject({ name: 'AbortError' });
    await entered.promise; let stopped = false; const stopping = f.manager[action]().then(() => { stopped = true; });
    try {
        await canceled.promise; expect(stopped).toBe(false); expect(completed).toBe(false);
        if (running && action === 'stop') expect(await f.manager.getRoute()).toEqual(f.route);
    } finally { release.resolve(); await stopping; await observed; }
    expect(completed).toBe(true); expect(failures).toEqual([]);
    if (action === 'stop') { f.resolveHook = undefined; expect(await f.manager.getRoute()).toEqual(f.route); }
    else { await f.manager.dispose(); await expect(f.manager.getRoute()).rejects.toThrow('disposing'); }
});

test.each(['older', 'not_found', 'failure'] as const)('an earlier %s refresh cannot overwrite a new publication or poison its freshness', async outcome => {
    const f = await fixture(); await f.manager.initialize(); const old = await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 7200 });
    const entered = deferred(); const release = deferred();
    f.resolveHook = async () => { entered.resolve(); await release.promise; if (outcome === 'failure') throw new TypeError('offline'); return outcome === 'older' ? old : undefined; };
    const refresh = f.manager.refreshRoute(); const observed = outcome === 'failure' ? expect(refresh).rejects.toThrow() : refresh;
    await entered.promise; let published!: AccountRoute;
    try { published = await f.manager.publishRoute(f.descriptor.relayId, { validitySeconds: 7200 }); expect(published.revision).toBe(old.revision + 1); }
    finally { release.resolve(); }
    await observed; if (outcome !== 'failure') expect(await refresh).toEqual(published);
    const reads = f.lookups; expect(await f.manager.getRoute()).toEqual(published); expect(f.lookups).toBe(reads);
    expect((await f.store.read([{ collection: 'account_routes', key: accountId }])).sets[0]![0]!.value).toEqual(accountRouteCodec.encode(published));
});

test('component isolation and nonblocking reuse of persisted routes after restart', async () => {
    const f = await fixture(); await f.manager.initialize(); f.route = f.makeRoute(1); await f.manager.getRoute();
    const other = await fixture(); await other.manager.initialize(); other.route = other.makeRoute(2);
    expect(await other.manager.getRoute()).toEqual(other.route); expect(other.lookups).toBe(1); expect(await f.manager.getRoute()).toEqual(f.route);
    await f.manager.dispose(); const reopened = new AccountManager({ context, accountId, store: f.store, relayClients: f.pool, clock: f.clock }); resources.push(reopened);
    await reopened.initialize(); const saved = f.route; expect(reopened.route).toEqual(saved); f.route = f.makeRoute(3);
    expect(await reopened.getRoute()).toEqual(saved); await reopened.refreshRoute(); expect(reopened.route).toEqual(f.route); expect(f.lookups).toBe(2);
});

test.each(['neo:860833103:0x5979ba79431672a38a18a32cdc48fd7317818b70', 'neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b71'])('a pool from another network or Registry cannot enter the route cache: %s', async value => {
    const f = await fixture();
    expect(() => new AccountManager({ context: NetworkContext.parse(value), accountId, store: f.store, relayClients: f.pool })).toThrow('another account or network');
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
