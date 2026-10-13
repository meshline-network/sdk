import { expect, test, vi } from 'vitest';
import { CachedRelayRegistry, NetworkContext } from '@meshline/sdk';
import { awaitWithSignal } from '../../packages/sdk/dist/runtime/clock.js';
import { AdvancingClock } from '../support/clock.js';
import { TestRegistry } from '../support/relay-fixture.js';

function deferred() { let resolve!: () => void; const promise = new Promise<void>(done => { resolve = done; }); return { promise, resolve }; }
async function directory(cache: CachedRelayRegistry) { const entries = []; for await (const entry of cache.getRelays()) entries.push(entry); return entries; }

test('registry entries coalesce cold reads, refresh on demand and isolate caller cancellation', async () => {
    const source = new TestRegistry(), clock = new AdvancingClock(), cache = new CachedRelayRegistry(source, clock);
    const entry = source.entry!, gate = deferred(); let transport!: AbortSignal;
    const read = vi.spyOn(source, 'getRelay').mockImplementation(async (...args: unknown[]) => { transport = args[1] as AbortSignal; await awaitWithSignal(gate.promise, transport); return entry; });
    const caller = new AbortController(); const first = cache.getRelay(entry.relayId, caller.signal).catch(error => error);
    const second = cache.getRelay(entry.relayId); await vi.waitFor(() => expect(read).toHaveBeenCalledTimes(1));
    caller.abort(); expect(await first).toMatchObject({ name: 'AbortError' }); expect(transport.aborted).toBe(false);
    gate.resolve(); expect(await second).toEqual(entry);
    for (let i = 0; i < 5; i++) await cache.getRelay(entry.relayId);
    clock.elapsed += 3_600_000; expect(read).toHaveBeenCalledTimes(1);
    const refresh = deferred(); read.mockImplementation(async () => { await refresh.promise; return { ...entry, status: 'disabled' }; });
    try {
        expect(await cache.getRelay(entry.relayId)).toEqual(entry);
        expect(await cache.getRelay(entry.relayId)).toEqual(entry); expect(read).toHaveBeenCalledTimes(2);
    } finally { refresh.resolve(); }
    await vi.waitFor(async () => expect((await cache.getRelay(entry.relayId))!.status).toBe('disabled'));
    await cache.dispose();
});

test('registry failures preserve a complete directory and allow demand retry after one minute', async () => {
    const source = new TestRegistry(), clock = new AdvancingClock(), cache = new CachedRelayRegistry(source, clock);
    const entry = source.entry!, errors = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    expect(await directory(cache)).toEqual([entry]);
    const reads = vi.spyOn(source, 'getRelays').mockImplementation(async function* () { yield { ...entry, status: 'disabled' }; throw new TypeError('offline'); });
    clock.elapsed += 3_600_000; expect(await directory(cache)).toEqual([entry]);
    await vi.waitFor(() => expect(errors).toHaveBeenCalled());
    expect(await directory(cache)).toEqual([entry]); clock.elapsed += 59_999; await directory(cache); expect(reads).toHaveBeenCalledTimes(1);
    clock.elapsed++; reads.mockImplementation(async function* () { yield { ...entry, status: 'disabled' }; });
    expect(await directory(cache)).toEqual([entry]);
    await vi.waitFor(async () => expect(await directory(cache)).toEqual([{ ...entry, status: 'disabled' }]));
    expect(reads).toHaveBeenCalledTimes(2); await cache.dispose();
});

test('a delayed entry cannot resurrect a newer directory deletion', async () => {
    const source = new TestRegistry(), clock = new AdvancingClock(), cache = new CachedRelayRegistry(source, clock), entry = source.entry!, gate = deferred();
    const read = vi.spyOn(source, 'getRelay').mockImplementation(async () => { await gate.promise; return entry; });
    const old = cache.getRelay(entry.relayId); await vi.waitFor(() => expect(read).toHaveBeenCalled());
    // The directory must have known the ID for its absence to be authoritative.
    expect(await directory(cache)).toEqual([entry]);
    source.entry = undefined; clock.elapsed += 3_600_000; await directory(cache);
    await vi.waitFor(async () => expect(await directory(cache)).toEqual([]));
    gate.resolve(); expect(await old).toBeUndefined(); await cache.dispose();
});

test('source boundaries, context changes and disposal are enforced without owning the source', async () => {
    const source = new TestRegistry(), cache = new CachedRelayRegistry(source), other = new CachedRelayRegistry(new TestRegistry());
    const entry = source.entry!; expect(await cache.getRelay(entry.relayId)).toEqual(entry);
    source.context = new NetworkContext(1, source.context.registry);
    await expect(cache.getRelay(entry.relayId)).rejects.toMatchObject({ code: 'context_changed' });
    expect(await other.getRelay(entry.relayId)).toEqual(entry);
    await cache.dispose(); await other.dispose(); await expect(cache.getRelay(entry.relayId)).rejects.toMatchObject({ name: 'AbortError' });
});
