import { expect, test } from 'vitest';
import { AsyncGate } from '../../packages/sdk/src/runtime/async-gate.js';

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
    controller.abort(reason); release(); await first; await rejected;
    expect(ran).toBe(false);
    await expect(gate.run(() => 'recovered', nativeController().signal)).resolves.toBe('recovered');
});
