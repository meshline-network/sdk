import { afterEach, expect, test, vi } from 'vitest';
import { AbortController as LegacyAbortController } from 'abort-controller';
import { abortScope, awaitWithSignal, systemClock, throwIfAborted } from '../../packages/sdk/dist/runtime/clock.js';
import { AsyncPulse } from '../../packages/sdk/dist/runtime/async-pulse.js';

const NativeAbortController = globalThis.AbortController;
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });
const legacy = () => vi.stubGlobal('AbortController', LegacyAbortController);

test('a deadline keeps its TimeoutError through nested scopes on the actual React Native abort-controller polyfill', async () => {
    legacy(); vi.useFakeTimers(); const parent = abortScope([], 25); const child = abortScope([parent.signal]);
    const settled = Promise.allSettled([awaitWithSignal(new Promise<void>(() => {}), child.signal)]);
    try {
        await vi.advanceTimersByTimeAsync(25); const result = (await settled)[0]!;
        expect(result.status).toBe('rejected'); if (result.status !== 'rejected') throw new Error('Missing cancellation');
        expect(result.reason).toBeInstanceOf(DOMException); expect(result.reason.name).toBe('TimeoutError');
        expect(child.signal.reason).toBe(result.reason); expect(parent.signal.reason).toBe(result.reason);
    } finally { child.dispose(); parent.dispose(); }
});

test('legacy caller cancellation produces one stable AbortError for gate, pulse and timer waiters', async () => {
    legacy(); const controller = new AbortController();
    const settled = Promise.allSettled([awaitWithSignal(new Promise<void>(() => {}), controller.signal), new AsyncPulse().wait(controller.signal), systemClock.delay(60000, controller.signal)]);
    controller.abort(); const results = await settled;
    for (const result of results) { expect(result.status).toBe('rejected'); if (result.status === 'rejected') expect(result.reason).toMatchObject({ name: 'AbortError' }); }
    const errors = results.filter(value => value.status === 'rejected').map(value => value.reason);
    expect(errors[0]).toBe(errors[1]); expect(errors[1]).toBe(errors[2]);
    let thrown: unknown; try { throwIfAborted(controller.signal); } catch (error) { thrown = error; } expect(thrown).toBe(errors[0]);
});

test.each([new Error('caller cancellation'), null, false, 'caller cancellation'])('a scope preserves the first caller reason verbatim: %s', async reason => {
    legacy(); const parent = new NativeAbortController(); const other = new NativeAbortController(); const scope = abortScope([parent.signal, other.signal]);
    const settled = Promise.allSettled([awaitWithSignal(new Promise<void>(() => {}), scope.signal)]);
    try {
        parent.abort(reason); other.abort(new Error('too late')); const result = (await settled)[0]!;
        expect(result.status).toBe('rejected'); if (result.status === 'rejected') expect(result.reason).toBe(reason);
        let thrown: unknown; try { throwIfAborted(scope.signal); } catch (error) { thrown = error; } expect(thrown).toBe(reason);
    } finally { scope.dispose(); }
});
