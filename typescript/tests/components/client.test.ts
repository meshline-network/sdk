import { afterEach, expect, test, vi } from 'vitest';
import { accountDeviceStateCodec, canonicalJson, certificateId, type ClientComponent } from '@meshline/sdk';
import { ClientNetwork } from '../support/client-network.js';
import { context } from '../support/relay-fixture.js';
const networks: ClientNetwork[] = []; afterEach(async () => { vi.restoreAllMocks(); for (const network of networks.splice(0)) await network.dispose(); });
async function fixture() { const network = new ClientNetwork(); networks.push(network); const local = await network.open(); return { network, ...local, a: network.relays[0]!.descriptor.relayId, b: network.relays[1]!.descriptor.relayId, c: network.relays[2]!.descriptor.relayId }; }
function children(client: Awaited<ReturnType<typeof fixture>>['client']): readonly ClientComponent[] { return [client.accountManager, client.deviceManager, client.profileManager, client.messageManager, client.channelManager, client.groupManager]; }
const operations = { collection: 'client_account_operations' };

test('client initialization does no network I/O, establishes authorization, owns child lifetimes and leaves shared resources open', async () => {
    const f = await fixture(); expect(f.network.requests).toEqual([]); expect(children(f.client).every(value => value.lifecycleState === 'stopped')).toBe(true);
    await f.client.establishAccount({ relayId: f.a, certificateValiditySeconds: 7200, routeValiditySeconds: 3600 }); expect(f.client.route!.relayId).toBe(f.a);
    expect(f.client.device!.expiresAt - f.client.device!.notBefore).toBe(7200); expect(f.client.route!.expiresAt - f.client.route!.updatedAt).toBe(3600);
    expect((await f.store.read([operations])).sets[0]).toEqual([]); await f.client.start(); expect(children(f.client).every(value => value.lifecycleState === 'running')).toBe(true);
    await f.client.stop(); expect(children(f.client).every(value => value.lifecycleState === 'stopped')).toBe(true); await f.client.dispose();
    expect(children(f.client).every(value => value.lifecycleState === 'disposed')).toBe(true); expect((await f.store.read([])).sets).toEqual([]); expect((await (await f.pool.get(f.a)).getDescriptor()).relayId).toBe(f.a);
});

test('failed startup rolls back all started children and a later authorized startup succeeds', async () => {
    const f = await fixture(); await expect(f.client.start()).rejects.toThrow('Create and authorize'); expect(f.client.lifecycleState).toBe('stopped'); expect(children(f.client).every(value => value.lifecycleState === 'stopped')).toBe(true);
    await f.client.establishAccount(); await f.client.start(); expect(f.client.lifecycleState).toBe('running');
});

test.each(['accountChanged', 'deviceChanged', 'deviceStateChanged'] as const)('a %s observer can dispose the owning client during establishment', async event => {
    const f = await fixture(); let release!: () => void; const escape = new Promise<void>(resolve => { release = resolve; });
    let disposal: Promise<void> | undefined; let completed = false;
    const observer = async () => { disposal ??= f.client.dispose(); void disposal.then(() => { completed = true; }); await Promise.race([disposal, escape]); };
    const remove = event === 'accountChanged' ? f.client.accountManager.on(event, observer) : f.client.deviceManager.on(event, observer);
    const establishing = f.client.establishAccount({ relayId: f.a }).catch(error => error);
    try { await vi.waitFor(() => expect(completed).toBe(true), { timeout: 1500 }); }
    finally { release(); remove(); await establishing; await disposal; }
    expect(f.client.lifecycleState).toBe('disposed'); expect(children(f.client).every(value => value.lifecycleState === 'disposed')).toBe(true);
    expect((await f.store.read([])).sets).toEqual([]);
});

test('a child running-state observer can stop the owning client while startup is completing', async () => {
    const f = await fixture(); await f.client.establishAccount({ relayId: f.a }); let release!: () => void; const escape = new Promise<void>(resolve => { release = resolve; });
    let stopping: Promise<void> | undefined; let completed = false;
    const remove = f.client.deviceManager.onLifecycle('stateChanged', async change => {
        if (change.current !== 'running') return;
        stopping = f.client.stop(); void stopping.then(() => { completed = true; }); await Promise.race([stopping, escape]);
    });
    const starting = f.client.start();
    try { await vi.waitFor(() => expect(completed).toBe(true), { timeout: 1500 }); }
    finally { release(); remove(); await starting; await stopping; }
    expect(f.client.lifecycleState).toBe('stopped'); expect(children(f.client).every(value => value.lifecycleState === 'stopped')).toBe(true);
});

test.each(['device.state.publish', 'account.route.publish'])('interrupted establishment resumes after %s acknowledgement loss with the original local keys', async method => {
    const f = await fixture(); f.network.loseAfter = method; await expect(f.client.establishAccount({ relayId: f.a })).rejects.toThrow('response lost'); const deviceId = certificateId(f.client.device!, context);
    const submitted = f.network.requests.filter(value => value.method === method)[0]!.body; expect((await f.store.read([operations])).sets[0]).toHaveLength(1);
    await f.dispose(); f.network.loseAfter = undefined; const resumed = await f.network.open(91, f.path); await resumed.client.establishAccount({ relayId: f.a });
    expect(certificateId(resumed.client.device!, context)).toBe(deviceId); expect(resumed.client.deviceState!.certificates).toHaveLength(1); expect((await resumed.store.read([operations])).sets[0]).toEqual([]);
    const attempts = f.network.requests.filter(value => value.method === method); expect(attempts).toHaveLength(method === 'account.route.publish' ? 1 : 2); expect(attempts.every(value => canonicalJson(value.body) === canonicalJson(submitted))).toBe(true);
});

test('pending initial establishment fixes the selected relay and accepted device publication cannot silently establish a route', async () => {
    const f = await fixture(); f.network.acceptedInitial = true; await expect(f.client.establishAccount({ relayId: f.a })).rejects.toThrow('already considers');
    await expect(f.client.establishAccount({ relayId: f.b })).rejects.toThrow('original relay'); expect(f.network.requests.filter(value => value.method === 'account.route.publish')).toEqual([]);
    await f.client.recoverAccount({ relayId: f.a }); expect(f.client.route!.relayId).toBe(f.a); expect((await f.store.read([operations])).sets[0]).toEqual([]);
});

test('existing-account establishment refuses an unregistered device; explicit recovery preserves other authorized devices', async () => {
    const f = await fixture(); await f.client.establishAccount({ relayId: f.a }); const first = certificateId(f.client.device!, context); const next = await f.network.open();
    await expect(next.client.establishAccount()).rejects.toThrow('explicitly recover'); expect(next.client.device).toBeUndefined();
    await next.client.recoverAccount(); expect(next.client.deviceState!.certificates.map(value => certificateId(value, context))).toEqual([first, certificateId(next.client.device!, context)]);
    expect(next.client.deviceState!.revision).toBeGreaterThanOrEqual(f.network.clock.wall * 1000); expect(next.client.route!.revision).toBeGreaterThanOrEqual(f.network.clock.wall * 1000);
});

test('foreign recovery snapshots and invalid validity settings fail before signing or creating a local device', async () => {
    const f = await fixture(); const other = await f.network.open(92); await other.client.establishAccount({ relayId: f.a }); const count = f.network.requests.length;
    await expect(f.client.recoverAccount({ previousDeviceState: other.client.deviceState! })).rejects.toThrow('another account');
    await expect(f.client.establishAccount({ certificateValiditySeconds: 0 })).rejects.toThrow(); await expect(f.client.recoverAccount({ routeValiditySeconds: 4000 * 86400 })).rejects.toThrow();
    expect(f.client.device).toBeUndefined(); expect(f.network.requests.length).toBe(count);
});

test('home migration stages the complete device state, activates the route and republishes the latest profile', async () => {
    const f = await fixture(); await f.client.establishAccount({ relayId: f.a, routeValiditySeconds: 4000 }); const next = await f.network.open(); await next.client.recoverAccount({ relayId: f.a, routeValiditySeconds: 4000 });
    await f.client.profileManager.updateProfile({ nickname: 'Migrated 😀', bio: '' }); await f.client.changeHomeRelay(f.b);
    expect(f.client.route!.relayId).toBe(f.b); expect(f.client.route!.expiresAt - f.client.route!.updatedAt).toBe(4000); expect(f.client.deviceState!.certificates).toHaveLength(2);
    expect(f.network.relays[1]!.profiles.get(f.client.accountId)!.profile.nickname).toBe('Migrated 😀'); expect(f.network.relays[1]!.profiles.get(f.client.accountId)!.profile.bio).toBe('');
    expect((await f.store.read([operations])).sets[0]).toEqual([]);
    const publications = f.network.requests.filter(value => value.relay === f.b && ['device.state.publish', 'account.route.publish', 'profile.publish'].includes(value.method));
    expect(publications.map(value => value.method)).toEqual(['device.state.publish', 'account.route.publish', 'profile.publish']);
});

test('startup resumes migration after accepted route and uncertain profile publication without republishing authorization or route', async () => {
    const f = await fixture(); await f.client.establishAccount({ relayId: f.a }); await f.client.profileManager.updateProfile({ nickname: 'Durable profile' }); f.network.loseAfter = 'profile.publish';
    await expect(f.client.changeHomeRelay(f.b)).rejects.toThrow('response lost'); expect(f.client.route!.relayId).toBe(f.b); expect((await f.store.read([operations])).sets[0]).toHaveLength(1);
    await f.dispose(); f.network.loseAfter = undefined; const resumed = await f.network.open(91, f.path); await resumed.client.start();
    expect(resumed.client.profile!.nickname).toBe('Durable profile'); expect((await resumed.store.read([operations])).sets[0]).toEqual([]);
    const attempts = f.network.requests.filter(value => value.relay === f.b); expect(attempts.filter(value => value.method === 'device.state.publish')).toHaveLength(1); expect(attempts.filter(value => value.method === 'account.route.publish')).toHaveLength(1);
    const profiles = attempts.filter(value => value.method === 'profile.publish'); expect(profiles).toHaveLength(2); expect(canonicalJson(profiles[0]!.body)).toBe(canonicalJson(profiles[1]!.body));
});

test('an unfinished migration rejects a different target and detects an external move to a third relay', async () => {
    const f = await fixture(); await f.client.establishAccount({ relayId: f.a }); f.network.failBefore = 'account.route.publish'; await expect(f.client.changeHomeRelay(f.b)).rejects.toThrow('unavailable');
    await expect(f.client.changeHomeRelay(f.c)).rejects.toThrow('original target'); f.network.failBefore = undefined;
    const other = await f.network.open(); await other.client.recoverAccount({ relayId: f.c }); await expect(f.client.changeHomeRelay(f.b)).rejects.toThrow('third relay'); expect(f.client.route!.relayId).toBe(f.c);
    expect((await f.store.read([operations])).sets[0]).toHaveLength(1);
});

test('migration refuses a short staging window and leaves the prior route intact for a later retry', async () => {
    const f = await fixture(); await f.client.establishAccount({ relayId: f.a }); f.network.stageSeconds = 59;
    await expect(f.client.changeHomeRelay(f.b)).rejects.toThrow('at least one minute'); expect(f.client.route!.relayId).toBe(f.a);
    expect(f.network.requests.filter(value => value.relay === f.b && value.method === 'account.route.publish')).toEqual([]);
    f.network.stageSeconds = 300; await f.client.changeHomeRelay(f.b); expect(f.client.route!.relayId).toBe(f.b);
});

test('cached complete authorization and profile permit source outage migration with visible diagnostics', async () => {
    const f = await fixture(); await f.client.establishAccount({ relayId: f.a }); await f.client.profileManager.updateProfile({ nickname: 'Cached source' });
    const before = accountDeviceStateCodec.stringify(f.client.deviceState!); f.network.unavailableRelay = f.a; const failures: string[] = [];
    f.client.onLifecycle('backgroundError', value => { failures.push(value.operation); }); await f.client.changeHomeRelay(f.b);
    expect(f.client.route!.relayId).toBe(f.b); expect(f.client.deviceState!.certificates).toHaveLength(1); expect(f.client.profile!.nickname).toBe('Cached source'); expect(before.length).toBeGreaterThan(0);
    expect(failures).toContain('resolve_migration_devices');
});

test('cleanup continues through a failing child and reports failure without abandoning remaining components', async () => {
    const f = await fixture(); await f.client.establishAccount(); await f.client.start(); const actual = f.client.groupManager.stop.bind(f.client.groupManager);
    vi.spyOn(f.client.groupManager, 'stop').mockImplementation(async () => { await actual(); throw new Error('Cleanup failed'); });
    await expect(f.client.stop()).rejects.toThrow('failed to stop'); expect(children(f.client).every(value => value.lifecycleState === 'stopped')).toBe(true); await f.client.dispose(); expect(children(f.client).every(value => value.lifecycleState === 'disposed')).toBe(true);
});

test('the staged migration route publication uses the clock deadline and retains its durable retry record on timeout', async () => {
    const f = await fixture(); await f.client.establishAccount({ relayId: f.a });
    f.network.beforeRoute = async () => { const deadline = [...f.network.clock.waiting].find(value => value.milliseconds === 60000)!; expect(deadline).toBeDefined(); deadline.complete(); await Promise.resolve(); };
    await expect(f.client.changeHomeRelay(f.b)).rejects.toThrow('deadline expired'); expect(f.client.route!.relayId).toBe(f.a); expect((await f.store.read([operations])).sets[0]).toHaveLength(1);
    f.network.beforeRoute = undefined; await f.client.changeHomeRelay(f.b); expect(f.client.route!.relayId).toBe(f.b); expect([...f.network.clock.waiting].filter(value => value.milliseconds === 60000)).toEqual([]);
});
