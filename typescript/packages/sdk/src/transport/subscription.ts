import { createAbortController } from '../runtime/abort.js';
import { ProtocolError } from '../errors.js';
import { canonicalJson, parseJson, requireObject, type JsonObject, type JsonValue } from '../protocol/json.js';
import { AsyncPulse } from '../runtime/async-pulse.js';
import { abortScope, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { RelayError } from './relay-error.js';

export type SubscriptionMethod = 'channel.subscribe' | 'group.subscribe';
export interface RelaySubscriptionOptions {
    /** Quiesces new attempts. The owner still calls dispose() after its producers stop. */
    readonly signal?: AbortSignal;
    readonly onSubscribed?: () => void | Promise<void>;
    readonly onError?: (error: unknown) => void | Promise<void>;
}
export interface SubscriptionConnection {
    readonly generation: number;
    readonly failure: unknown;
    request(method: string, parameters: JsonObject, signal: AbortSignal): Promise<JsonValue>;
    retire(error: unknown): void;
}
export interface SubscriptionTransport {
    readonly clock: RuntimeClock;
    readonly lifetime: AbortSignal;
    ready(signal: AbortSignal): Promise<SubscriptionConnection>;
    onConnected(listener: () => void): () => void;
    closed(): void;
}
function transient(error: unknown): boolean {
    if (error instanceof RelayError) return ['unauthorized', 'temporarily_unavailable', 'bad_gateway', 'rate_limited'].includes(error.code);
    if (error instanceof ProtocolError) return ['socket_closed', 'socket_error', 'notification_overflow'].includes(error.code);
    return error instanceof TypeError || error instanceof Error && (['AbortError', 'TimeoutError'].includes(error.name) || 'code' in error);
}

/** One full-replacement stream for one method and relay. Created through RelayClient. */
export class RelaySubscription {
    readonly #wake = new AsyncPulse(); readonly #stop = createAbortController(); readonly #pending = createAbortController();
    readonly #scope: ReturnType<typeof abortScope>; readonly #pendingScope: ReturnType<typeof abortScope>;
    readonly #empty: string; readonly #detach: () => void;
    #desired: string | undefined; #worker: Promise<void> | undefined; #connection: SubscriptionConnection | undefined;
    #applied: string | undefined; #appliedGeneration: number | undefined; #lastError: unknown; #disposal: Promise<void> | undefined;
    constructor(readonly method: SubscriptionMethod, empty: JsonObject, readonly options: RelaySubscriptionOptions, readonly transport: SubscriptionTransport) {
        this.#empty = canonicalJson(requireObject(empty));
        this.#scope = abortScope([options.signal, transport.lifetime, this.#stop.signal]);
        this.#pendingScope = abortScope([transport.lifetime, this.#pending.signal]);
        this.#detach = transport.onConnected(() => this.#wake.pulse());
    }
    get lastError(): unknown { return this.#lastError; }
    /** Snapshots and coalesces updates, including empty sets; it does not wait for remote acknowledgement. */
    update(request: JsonObject): void {
        throwIfAborted(this.#scope.signal);
        this.#desired = canonicalJson(requireObject(request)); this.#wake.pulse();
        this.#worker ??= this.#run();
    }
    #report(error: unknown): void {
        this.#lastError = error;
        try { Promise.resolve(this.options.onError?.(error)).catch(observer => { this.#lastError = new AggregateError([error, observer], 'Subscription and error observer failed.'); }); }
        catch (observer) { this.#lastError = new AggregateError([error, observer], 'Subscription and error observer failed.'); }
    }
    #subscribed(): void {
        try { Promise.resolve(this.options.onSubscribed?.()).catch(error => this.#report(error)); }
        catch (error) { this.#report(error); }
    }
    async #send(connection: SubscriptionConnection, json: string): Promise<void> {
        throwIfAborted(this.#pendingScope.signal);
        try {
            const result = await connection.request(this.method, requireObject(parseJson(json)), this.#pendingScope.signal);
            if (result !== null) throw new ProtocolError('invalid_response', 'Subscription replacement must acknowledge with JSON null.');
        } catch (error) {
            if (!(error instanceof RelayError)) connection.retire(new ProtocolError('socket_closed', 'Subscription outcome is unknown; reconnect before replacing the set.', { cause: error }));
            throw error;
        }
    }
    async #retry(): Promise<void> {
        const timer = createAbortController(); const scope = abortScope([this.#scope.signal, timer.signal]);
        const delay = this.transport.clock.delay(5000, scope.signal).then(() => this.#wake.pulse(), error => { if (!scope.signal.aborted) this.#report(error); });
        try { await this.#wake.wait(this.#scope.signal); this.#wake.pulse(); }
        finally { timer.abort(); await delay; scope.dispose(); }
    }
    async #run(): Promise<void> {
        try { for (;;) {
            await this.#wake.wait(this.#scope.signal);
            try {
                const connection = await this.transport.ready(this.#scope.signal);
                // Take the latest set only after this relay becomes ready.
                const request = this.#desired; if (request === undefined) continue;
                if (this.#applied === request && this.#appliedGeneration === connection.generation) continue;
                throwIfAborted(this.#scope.signal); this.#applied = undefined; this.#connection = connection;
                await this.#send(connection, request); throwIfAborted(this.#scope.signal);
                this.#applied = request; this.#appliedGeneration = connection.generation;
                if (request !== this.#empty) this.#subscribed();
            } catch (error) {
                if (this.#scope.signal.aborted) { if (this.#pendingScope.signal.aborted && !this.transport.lifetime.aborted) this.#report(error); break; }
                this.#report(error); if (transient(error)) await this.#retry();
            }
        } } catch (error) { if (!this.#scope.signal.aborted) this.#report(error); }
    }
    /** Drains the in-flight replacement and clears this exact connection within one shared five-second budget. */
    dispose(): Promise<void> {
        return this.#disposal ??= (async () => {
            this.#stop.abort(new DOMException('Subscription stopped.', 'AbortError')); this.#detach();
            const timer = createAbortController();
            const budget = this.transport.clock.delay(5000, timer.signal).then(() => this.#pending.abort(new DOMException('Subscription cleanup timed out.', 'TimeoutError')), error => {
                if (!timer.signal.aborted) { this.#pending.abort(error); this.#report(error); }
            });
            try {
                await this.#worker;
                const connection = this.#connection;
                if (connection && !connection.failure) {
                    try { await this.#send(connection, this.#empty); }
                    catch (error) { connection.retire(new ProtocolError('socket_closed', 'Subscriptions could not be cleared; retire the old connection.', { cause: error })); this.#report(error); }
                }
            } finally {
                timer.abort(); await budget; this.#scope.dispose(); this.#pendingScope.dispose(); this.transport.closed();
            }
        })();
    }
}
