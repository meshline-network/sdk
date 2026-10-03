import { expect, test, vi } from 'vitest';
import { ClientComponent, NetworkContext, type BackgroundFailure, type ClientOptions } from '@meshline/sdk';

const options: ClientOptions = { context: NetworkContext.parse('neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70'), accountId: 'neo:860833102:NgaxELHoZFpQWNwd74Fvq4wF3qz57WTfpp' };
function deferred() { let resolve!: () => void; const promise = new Promise<void>(yes => { resolve = yes; }); return { promise, resolve }; }
class Component extends ClientComponent {
    initialized = 0; started = 0; stopped = 0; cleaned = 0;
    initializeHook = async (_signal: AbortSignal): Promise<void> => {};
    startHook = async (_signal: AbortSignal): Promise<void> => {};
    stopHook = async (): Promise<void> => {};
    constructor() { super(options); }
    get runtime(): AbortSignal { return this.runtimeSignal; }
    operation(action: (signal: AbortSignal) => Promise<void>, signal?: AbortSignal) { return this.runOperation(action, signal); }
    fail(failure: BackgroundFailure) { return this.reportBackgroundError(failure); }
    protected override async onInitialize(signal: AbortSignal) { this.initialized++; await this.initializeHook(signal); }
    protected override async onStart(signal: AbortSignal) { this.started++; await this.startHook(signal); }
    protected override async onStop() { this.stopped++; await this.stopHook(); }
    protected override async onDispose() { this.cleaned++; }
}

test('explicit initialization is coalesced, failures are retryable, and hooks do no constructor I/O', async () => {
    const component = new Component(); expect(component.initialized).toBe(0);
    expect(() => component.operation(async () => {})).toThrow('Initialize');
    component.initializeHook = async () => { throw new Error('schema missing'); };
    await expect(component.initialize()).rejects.toThrow('schema missing'); expect(component.lifecycleState).toBe('uninitialized');
    component.initializeHook = async () => {};
    await Promise.all([component.initialize(), component.initialize(), component.initialize()]);
    expect(component.initialized).toBe(2); expect(component.lifecycleState).toBe('stopped'); await component.dispose();
});

test('startup caller cancellation does not cancel a successfully started runtime; stop allows restart', async () => {
    const component = new Component(); await component.initialize(); const caller = new AbortController();
    await Promise.all([component.start(caller.signal), component.start()]); const firstRuntime = component.runtime;
    caller.abort(); expect(firstRuntime.aborted).toBe(false); expect(component.started).toBe(1);
    await component.stop(); expect(firstRuntime.aborted).toBe(true);
    await component.start(); expect(component.runtime).not.toBe(firstRuntime); expect(component.started).toBe(2);
    await component.dispose(); expect(component.stopped).toBe(2);
});

test('failed startup drains runtime, including cleanup failures, and can be retried', async () => {
    const component = new Component(); await component.initialize(); let failedRuntime: AbortSignal | undefined;
    component.startHook = async () => { failedRuntime = component.runtime; throw new Error('start failed'); };
    component.stopHook = async () => { throw new Error('stop failed'); };
    await expect(component.start()).rejects.toMatchObject({ errors: [expect.objectContaining({ message: 'start failed' }), expect.objectContaining({ message: 'stop failed' })] });
    expect(failedRuntime!.aborted).toBe(true); expect(component.lifecycleState).toBe('stopped');
    component.startHook = async () => {}; component.stopHook = async () => {};
    await component.start(); await component.dispose();
});

test('stop drains cleanup after caller cancellation and retains foreground operations', async () => {
    const component = new Component(); await component.initialize(); await component.start();
    const cleanup = deferred(); component.stopHook = async () => cleanup.promise;
    const foreground = deferred(); let foregroundSignal!: AbortSignal;
    const operation = component.operation(async signal => { foregroundSignal = signal; await foreground.promise; });
    const caller = new AbortController(); const stop = component.stop(caller.signal);
    await vi.waitFor(() => expect(component.lifecycleState).toBe('stopping'));
    caller.abort(); expect(foregroundSignal.aborted).toBe(false); cleanup.resolve(); await stop;
    foreground.resolve(); await operation; await component.dispose();
});

test('disposal cancels initialization promptly and waits for foreground finally blocks', async () => {
    const initializing = new Component();
    initializing.initializeHook = signal => new Promise((_, reject) => signal.addEventListener('abort', () => reject(signal.reason), { once: true }));
    const initialized = initializing.initialize(); const rejection = expect(initialized).rejects.toMatchObject({ name: 'AbortError' });
    await vi.waitFor(() => expect(initializing.initialized).toBe(1)); await initializing.dispose(); await rejection;
    expect(initializing.lifecycleState).toBe('disposed');
    const component = new Component(); await component.initialize(); const cleanup = deferred(); let aborted = false;
    const operation = component.operation(async signal => {
        try { await new Promise((_, reject) => signal.addEventListener('abort', () => { aborted = true; reject(signal.reason); }, { once: true })); }
        finally { await cleanup.promise; }
    });
    const rejected = expect(operation).rejects.toMatchObject({ name: 'AbortError' });
    await Promise.resolve(); const dispose = component.dispose(); expect(component.dispose()).toBe(dispose);
    expect(() => component.operation(async () => {})).toThrow('disposing');
    await vi.waitFor(() => expect(aborted).toBe(true)); expect(component.cleaned).toBe(0);
    cleanup.resolve(); await dispose; await rejected; expect(component.cleaned).toBe(1);
});

test('state observers run after the lifecycle lock releases and failures remain visible', async () => {
    const component = new Component(); const observed: string[] = [];
    component.onLifecycle('stateChanged', async change => { observed.push(change.current); if (change.current === 'running') await component.stop(); });
    await component.initialize(); await component.start(); await vi.waitFor(() => expect(component.lifecycleState).toBe('stopped'));
    expect(observed).toEqual(['stopped', 'running', 'stopped']);
    component.onLifecycle('stateChanged', () => { throw new Error('observer failed'); });
    await component.dispose(); expect(component.lifecycleState).toBe('disposed');
    expect(component.lastBackgroundError).toMatchObject({ operation: 'observer', resource: 'stateChanged', error: { errors: [expect.objectContaining({ message: 'observer failed' })] } });
});

test('disposed-state observers can await repeated disposal and later subscribers still run', async () => {
    const component = new Component(); await component.initialize(); const pending = deferred(); let completed = false; let later = false;
    component.onLifecycle('stateChanged', () => pending.promise);
    component.onLifecycle('stateChanged', async change => { if (change.current === 'disposed') { await component.dispose(); completed = true; } });
    component.onLifecycle('stateChanged', () => { later = true; });
    try { await component.dispose(); await vi.waitFor(() => expect(completed).toBe(true)); expect(later).toBe(true); }
    finally { pending.resolve(); }
});

test('background-error observers can dispose their owner and late failures remain visible', async () => {
    const component = new Component(); await component.initialize(); const pending = deferred(); let completed = false;
    const original = new Error('runtime failure'); const observer = new Error('error observer failure');
    component.onLifecycle('backgroundError', () => pending.promise);
    component.onLifecycle('backgroundError', async () => { await component.dispose(); completed = true; throw observer; });
    try {
        await component.fail({ operation: 'synchronize', error: original });
        await vi.waitFor(() => expect(component.lastBackgroundError).toMatchObject({ operation: 'synchronize', error: { errors: [original, expect.objectContaining({ errors: [observer] })] } }));
        expect(completed).toBe(true); expect(component.lifecycleState).toBe('disposed');
    } finally { pending.resolve(); await component.dispose(); }
});
