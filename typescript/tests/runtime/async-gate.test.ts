import { expect, test } from 'vitest';
import { AsyncGate } from '../../packages/sdk/dist/runtime/async-gate.js';

function nativeController(): AbortController {
    const controller = new AbortController();
    Object.defineProperty(controller.signal, 'throwIfAborted', { value: undefined });
    return controller;
}

test('the gate accepts a React Native AbortSignal without throwIfAborted', async () => {
    const gate = new AsyncGate(); const controller = nativeController();
    await expect(gate.run(() => 'completed', controller.signal)).resolves.toBe('completed');
});

test('queued native cancellation preserves the reason and does not poison later operations', async () => {
    const gate = new AsyncGate(); const controller = nativeController(); const reason = new Error('native cancellation');
    let release!: () => void; let ran = false;
    const first = gate.run(() => new Promise<void>(resolve => { release = resolve; }));
    await Promise.resolve();
    const second = gate.run(() => { ran = true; }, controller.signal);
    const rejected = expect(second).rejects.toBe(reason);
    controller.abort(reason); await rejected;
    expect(ran).toBe(false);
    let thirdRan = false;
    const third = gate.run(() => { thirdRan = true; return 'recovered'; }, nativeController().signal);
    await Promise.resolve(); expect(thirdRan).toBe(false);
    release(); await first; await expect(third).resolves.toBe('recovered');
});

test('canceling an active operation still waits for it to finish before releasing the caller and queue', async () => {
    const gate = new AsyncGate(); const controller = nativeController();
    let release!: () => void; let entered!: () => void; const started = new Promise<void>(resolve => { entered = resolve; });
    let completed = false; let nextRan = false;
    const active = gate.run(async () => { entered(); await new Promise<void>(resolve => { release = resolve; }); }, controller.signal).then(() => { completed = true; });
    await started; controller.abort();
    const next = gate.run(() => { nextRan = true; });
    await Promise.resolve(); expect(completed).toBe(false); expect(nextRan).toBe(false);
    release(); await active; await next;
});
