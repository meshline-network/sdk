import { expect, test } from 'vitest';
import { ProtocolError, type ResourceSyncStatus } from '@meshline/sdk';
import { ResourceSyncTracker } from '../../packages/sdk/dist/runtime/resource-sync.js';
import { MessagingClock } from '../portable/clock.js';

test('superseded completion and cancellation cannot erase a newer block or invent success', async () => {
    const clock = new MessagingClock(); const changes: ResourceSyncStatus[] = []; const tracker = new ResourceSyncTracker(clock, status => changes.push(status));
    let finish!: () => void; const pending = new Promise<void>(resolve => { finish = resolve; }); const signal = new AbortController().signal;
    const old = tracker.run('one', async () => { await pending; return undefined; }, signal);
    expect(tracker.get('one').lastSynchronizedAt).toBeUndefined(); const error = new ProtocolError('invalid_storage', 'disk failure');
    await expect(tracker.run('one', async () => { throw error; }, signal)).rejects.toBe(error);
    finish(); await old; expect(tracker.get('one')).toMatchObject({ state: 'blocked', blockReason: 'storage', error });
    expect(tracker.get('one').lastSynchronizedAt).toBeUndefined();
    const cancellation = new AbortController(); const stopped = tracker.run('two', async () => { cancellation.abort(); return undefined; }, cancellation.signal);
    await expect(stopped).rejects.toBeDefined(); expect(tracker.get('two').state).toBe('idle'); expect(tracker.get('two').error).toBeUndefined();
    const another = tracker.run('one', async () => { tracker.stop(); return undefined; }, signal); await another;
    expect(tracker.get('one').state).toBe('idle'); expect(changes.filter(status => status.state === 'caughtUp')).toEqual([]);
});

test.each(['success', 'failure', 'cancel'] as const)('manual %s remains observable after stop, while disposal invalidates it', async outcome => {
    const clock = new MessagingClock(); const changes: ResourceSyncStatus[] = [];
    const tracker = new ResourceSyncTracker(clock, value => { changes.push(value); }); const controller = new AbortController();
    let release!: () => void; const pending = new Promise<void>(resolve => { release = resolve; });
    const error = new ProtocolError('invalid_storage', 'disk failure');
    const pass = tracker.run('manual', async () => { await pending; if (outcome === 'failure') throw error; return undefined; }, controller.signal, true);
    tracker.observeGap('manual'); tracker.stop(); expect(tracker.get('manual').state).toBe('synchronizing');
    if (outcome === 'cancel') controller.abort();
    release();
    if (outcome === 'success') expect((await pass).state).toBe('caughtUp'); else await expect(pass).rejects.toBeDefined();
    const completed = tracker.get('manual');
    expect(completed.state).toBe(outcome === 'success' ? 'caughtUp' : outcome === 'failure' ? 'blocked' : 'idle');
    expect(completed.hasRetentionGap).toBe(true); expect(changes.at(-1)).toBe(completed);
    tracker.stop(); expect(tracker.get('manual')).toBe(completed);
    const disposed = tracker.run('manual', async () => { tracker.stop(true); return undefined; }, new AbortController().signal, true);
    await disposed; expect(tracker.get('manual').state).toBe('idle'); expect(tracker.get('manual').lastSynchronizedAt).toBe(completed.lastSynchronizedAt);
});
