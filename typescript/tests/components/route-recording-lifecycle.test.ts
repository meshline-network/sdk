import { afterEach, expect, test, vi } from 'vitest';
import type { BackgroundFailure } from '@meshline/sdk';
import { ClientNetwork } from '../support/client-network.js';

const networks: ClientNetwork[] = [];
afterEach(async () => {
    vi.restoreAllMocks();
    for (const network of networks.splice(0)) await network.dispose();
});
function deferred() {
    let resolve!: () => void;
    const promise = new Promise<void>(yes => { resolve = yes; });
    return { promise, resolve };
}
async function fixture() {
    const network = new ClientNetwork();
    networks.push(network);
    const local = await network.open();
    const a = network.relays[0]!.descriptor.relayId;
    const b = network.relays[1]!.descriptor.relayId;
    await local.client.establishAccount({ relayId: a });
    await local.client.messageManager.stop();
    network.relays[1]!.states.set(local.client.accountId, local.client.deviceState!);
    return { network, ...local, a, b };
}
const waitFor = (check: () => void | Promise<void>) => vi.waitFor(check, { timeout: 5000 });
const turn = () => new Promise<void>(resolve => setTimeout(resolve, 0));

test('client stop reports an already disposed message component once', async () => {
    const f = await fixture();
    await f.client.start();
    await f.client.messageManager.dispose();
    let checked = false;
    try {
        const failure = await f.client.stop().catch(error => error);
        expect(failure).toBeInstanceOf(AggregateError);
        expect((failure as AggregateError).errors).toHaveLength(1);
        expect((failure as AggregateError).errors[0]).toMatchObject({ code: 'disposed' });
        expect(f.client.lifecycleState).toBe('stopped');
        checked = true;
    } finally {
        const failure = await f.client.dispose().catch(error => error);
        // Disposal retains its rejected promise; assert it here before retiring the fixture entry.
        const index = f.network.resources.indexOf(f.client);
        if (index >= 0) f.network.resources.splice(index, 1);
        if (checked) {
            expect(failure).toBeInstanceOf(AggregateError);
            expect((failure as AggregateError).errors).toHaveLength(1);
            const stopFailure: unknown = (failure as AggregateError).errors[0];
            expect(stopFailure).toBeInstanceOf(AggregateError);
            expect((stopFailure as AggregateError).errors).toHaveLength(1);
            expect((stopFailure as AggregateError).errors[0]).toMatchObject({ code: 'disposed' });
            expect(f.client.lifecycleState).toBe('disposed');
        }
    }
});

test.each([[false, false], [true, false], [false, true], [true, true]])('client stop drains routes produced by later children (write error %s, stop error %s)', async (failWrite, failStop) => {
    const f = await fixture();
    await f.client.start();
    await waitFor(async () => expect((await f.client.messageManager.getSyncStatus(f.a)).state).toBe('caughtUp'));
    f.network.failBefore = 'account.route.publish';
    await expect(f.client.accountManager.publishRoute(f.b, { validitySeconds: 86400 })).rejects.toThrow();
    f.network.failBefore = undefined;
    const publish = deferred();
    let publishing = false;
    f.network.beforeRoute = async () => { publishing = true; await publish.promise; };
    const proceed = deferred();
    let stoppingDevice = false;
    const stopError = new Error('device stop failed');
    const deviceStop = f.client.deviceManager.stop.bind(f.client.deviceManager);
    const stopSpy = vi.spyOn(f.client.deviceManager, 'stop').mockImplementationOnce(async signal => {
        stoppingDevice = true;
        await proceed.promise;
        await deviceStop(signal);
        if (failStop) throw stopError;
    });
    const release = deferred();
    let writing = false;
    const writeError = new Error('timeline write failed');
    const reported: BackgroundFailure[] = [];
    const detach = f.client.messageManager.onLifecycle('backgroundError', value => { reported.push(value); });
    const commit = f.store.commit.bind(f.store);
    const writeSpy = vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (mutations.some(value => value.collection === 'message_timelines' && value.key === f.b)) {
            writing = true;
            await release.promise;
            if (failWrite) throw writeError;
        }
        return commit(version, mutations, signal);
    });
    let stopping: Promise<unknown> | undefined;
    let completed = false;
    try {
        f.network.clock.tick();
        await waitFor(() => expect(publishing).toBe(true));
        stopping = f.client.stop().then(() => undefined, error => error).finally(() => { completed = true; });
        await waitFor(() => expect(stoppingDevice).toBe(true));
        publish.resolve();
        await waitFor(() => expect(writing).toBe(true));
        proceed.resolve();
        await waitFor(() => expect(f.client.accountManager.lifecycleState).toBe('stopped'));
        await turn();
        expect(completed).toBe(false);

        release.resolve();
        const failure = await stopping;
        if (failStop) expect(failure).toMatchObject({ errors: [stopError] }); else expect(failure).toBeUndefined();
        if (failWrite) expect(reported).toContainEqual({ operation: 'record_route', resource: f.b, error: writeError });
        expect(f.client.lifecycleState).toBe('stopped');
        writeSpy.mockRestore();
        stopSpy.mockRestore();
        f.network.beforeRoute = undefined;
        await f.client.start();
        await waitFor(async () => expect((await f.client.messageManager.getSyncStatus(f.b)).state).toBe('caughtUp'));
        await f.client.stop();
        expect((await f.store.read([{ collection: 'message_timelines', key: f.b }])).sets[0]).toHaveLength(1);
    } finally {
        publish.resolve();
        proceed.resolve();
        release.resolve();
        writeSpy.mockRestore();
        stopSpy.mockRestore();
        detach();
        f.network.beforeRoute = undefined;
        await stopping;
    }
}, 15000);

test('standalone message stop drains route recording before any runtime has started', async () => {
    const f = await fixture();
    const release = deferred();
    let writing = false;
    let completed = false;
    const commit = f.store.commit.bind(f.store);
    const spy = vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (mutations.some(value => value.collection === 'message_timelines' && value.key === f.b)) {
            writing = true;
            await release.promise;
        }
        return commit(version, mutations, signal);
    });
    let stopping: Promise<void> | undefined;
    try {
        await f.client.accountManager.publishRoute(f.b, { validitySeconds: 86400 });
        await waitFor(() => expect(writing).toBe(true));
        stopping = f.client.messageManager.stop().then(() => { completed = true; });
        await turn();
        expect(completed).toBe(false);
        release.resolve();
        await stopping;
        expect((await f.store.read([{ collection: 'message_timelines', key: f.b }])).sets[0]).toHaveLength(1);
    } finally {
        release.resolve();
        spy.mockRestore();
        await stopping;
    }
});

test('disposal retries a pending route observation after storage recovers', async () => {
    const f = await fixture();
    const failure = new Error('timeline unavailable');
    const reported: BackgroundFailure[] = [];
    const detach = f.client.messageManager.onLifecycle('backgroundError', value => { reported.push(value); });
    const commit = f.store.commit.bind(f.store);
    const spy = vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (mutations.some(value => value.collection === 'message_timelines' && value.key === f.b)) throw failure;
        return commit(version, mutations, signal);
    });
    try {
        await f.client.accountManager.publishRoute(f.b, { validitySeconds: 86400 });
        await waitFor(() => expect(reported).toContainEqual({ operation: 'record_route', resource: f.b, error: failure }));
    } finally { spy.mockRestore(); detach(); }
    await f.client.dispose();
    expect((await f.store.read([{ collection: 'message_timelines', key: f.b }])).sets[0]).toHaveLength(1);
});
