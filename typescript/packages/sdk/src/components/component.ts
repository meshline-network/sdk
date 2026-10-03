import { createAbortController } from '../runtime/abort.js';
import { ProtocolError } from '../errors.js';
import { validateAccountId } from '../identity/neo.js';
import type { NetworkContext } from '../protocol/context.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { abortScope, systemClock, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';

export type ComponentState = 'uninitialized' | 'stopped' | 'running' | 'stopping' | 'disposed';
export interface ClientOptions { readonly context: NetworkContext; readonly accountId: string; readonly clock?: RuntimeClock }
export interface BackgroundFailure { readonly operation: string; readonly resource?: string; readonly error: unknown }
export interface ComponentEvents {
    readonly stateChanged: { readonly previous: ComponentState; readonly current: ComponentState };
    readonly backgroundError: BackgroundFailure;
}

/** Constructors do no I/O. The application owns shared stores, signers and relay pools. */
export abstract class ClientComponent {
    readonly #gate = new AsyncGate();
    readonly #lifetime = createAbortController();
    readonly #operations = new Set<Promise<unknown>>();
    readonly #events = new EventHub<ComponentEvents>();
    #runtime: AbortController | undefined;
    #state: ComponentState = 'uninitialized';
    #disposing = false;
    #disposal: Promise<void> | undefined;
    #backgroundFailure: BackgroundFailure | undefined;
    readonly context: NetworkContext;
    readonly accountId: string;
    protected readonly clock: RuntimeClock;

    protected constructor(options: ClientOptions) {
        validateAccountId(options.accountId);
        this.context = options.context; this.accountId = options.accountId; this.clock = options.clock ?? systemClock;
    }
    get lifecycleState(): ComponentState { return this.#state; }
    get lastBackgroundError(): BackgroundFailure | undefined { return this.#backgroundFailure; }
    onLifecycle<K extends keyof ComponentEvents>(event: K, listener: EventListener<ComponentEvents[K]>): () => void { return this.#events.on(event, listener); }
    #stateChanged(previous: ComponentState, current: ComponentState): void {
        this.#events.notify('stateChanged', { previous, current }, error => this.notifyBackgroundError({ operation: 'observer', resource: 'stateChanged', error }));
    }
    protected get runtimeSignal(): AbortSignal {
        if (!this.#runtime) throw new ProtocolError('not_running', 'The component has not started.');
        return this.#runtime.signal;
    }
    #check(): void { if (this.#disposing) throw new ProtocolError('disposed', 'The component is disposing or disposed.'); }
    ensureInitialized(): void {
        this.#check();
        if (this.#state === 'uninitialized') throw new ProtocolError('not_initialized', 'Initialize the component before using it.');
    }
    async initialize(signal?: AbortSignal): Promise<void> {
        this.#check();
        const scope = abortScope([signal, this.#lifetime.signal]);
        let changed = false;
        try {
            await this.#gate.run(async () => {
                this.#check();
                if (this.#state !== 'uninitialized') return;
                await this.onInitialize(scope.signal); throwIfAborted(scope.signal);
                this.#state = 'stopped'; changed = true;
            }, scope.signal);
        } finally { scope.dispose(); }
        if (changed) this.#stateChanged('uninitialized', 'stopped');
    }
    async start(signal?: AbortSignal): Promise<void> {
        this.ensureInitialized();
        const scope = abortScope([signal, this.#lifetime.signal]);
        let changed = false;
        try {
            await this.#gate.run(async () => {
                this.ensureInitialized();
                if (this.#state === 'running') return;
                this.#runtime = createAbortController();
                try {
                    await this.onStart(scope.signal); throwIfAborted(scope.signal);
                } catch (error) {
                    try { await this.#endRuntime(); } catch (cleanup) { throw new AggregateError([error, cleanup], 'Startup and cleanup failed.'); }
                    throw error;
                }
                this.#state = 'running'; changed = true;
            }, scope.signal);
        } finally { scope.dispose(); }
        if (changed) this.#stateChanged('stopped', 'running');
    }
    async stop(signal?: AbortSignal): Promise<void> {
        this.#check();
        let changed = false;
        try {
            await this.#gate.run(async () => {
                this.#check();
                if (this.#state !== 'running') return;
                this.#state = 'stopping';
                try { await this.#endRuntime(); }
                finally { this.#state = 'stopped'; changed = true; }
            }, signal);
        } finally {
            if (changed) this.#stateChanged('running', 'stopped');
        }
    }
    async #endRuntime(): Promise<void> {
        if (!this.#runtime) return;
        this.#runtime.abort(new DOMException('Component runtime stopped.', 'AbortError'));
        try { await this.onStop(); } finally { this.#runtime = undefined; }
    }
    /** Derived public operations use this scope; emit observer events after it releases. */
    protected runOperation<T>(action: (signal: AbortSignal) => Promise<T>, signal?: AbortSignal): Promise<T> {
        this.ensureInitialized(); throwIfAborted(signal);
        const scope = abortScope([signal, this.#lifetime.signal]);
        const job = Promise.resolve().then(() => { throwIfAborted(scope.signal); return action(scope.signal); });
        this.#operations.add(job);
        const released = (): void => { scope.dispose(); this.#operations.delete(job); };
        job.then(released, released);
        return job;
    }
    protected reportBackgroundError(failure: BackgroundFailure): Promise<void> {
        this.#backgroundFailure = Object.freeze({ ...failure });
        this.#events.notify('backgroundError', failure, observerError => {
            this.#backgroundFailure = Object.freeze({ ...failure, error: new AggregateError([failure.error, observerError], 'Background operation and error observer failed.') });
        });
        return Promise.resolve();
    }
    /** Runtime jobs must not wait for application handlers that can stop or dispose that same runtime. */
    protected notifyBackgroundError(failure: BackgroundFailure): void {
        void this.reportBackgroundError(failure);
    }
    dispose(): Promise<void> {
        if (this.#disposal) return this.#disposal;
        this.#disposing = true;
        this.#lifetime.abort(new DOMException('Component disposed.', 'AbortError'));
        this.#runtime?.abort(new DOMException('Component disposed.', 'AbortError'));
        this.#disposal = this.#gate.run(async () => {
            const previous = this.#state;
            const errors: unknown[] = [];
            try { await this.#endRuntime(); } catch (error) { errors.push(error); }
            // Foreground failures belong to their callers; disposal only waits for cleanup.
            await Promise.allSettled([...this.#operations]);
            try { await this.onDispose(); } catch (error) { errors.push(error); }
            this.#state = 'disposed';
            return { previous, errors };
        }).then(({ previous, errors }) => {
            this.#stateChanged(previous, 'disposed'); this.#events.clear();
            if (errors.length) throw new AggregateError(errors, 'Component disposal failed.');
        });
        return this.#disposal;
    }
    protected async onInitialize(_signal: AbortSignal): Promise<void> {}
    /** Use runtimeSignal for background jobs; signal only controls startup. */
    protected async onStart(_signal: AbortSignal): Promise<void> {}
    protected async onStop(): Promise<void> {}
    protected async onDispose(): Promise<void> {}
}
