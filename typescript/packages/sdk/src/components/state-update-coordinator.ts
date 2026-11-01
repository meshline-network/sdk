import { ProtocolError, StateConflictError } from '../errors.js';
import { canonicalJson, parseJson, requireObject, type JsonObject } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { AsyncPulse } from '../runtime/async-pulse.js';
import { createAbortController } from '../runtime/abort.js';
import { abortScope, awaitWithSignal, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { publicationState, PublicationRequestState, supersedeRequest } from '../storage/state-publication.js';
import type { MeshlineStore, RecordKey, RecordQuery, StoredRecord } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { RelayError } from '../transport/relay-error.js';
import { StateUpdateSupersededError } from './state-update-error.js';

const copy = (value: JsonObject): JsonObject => requireObject(parseJson(canonicalJson(value)));
interface AttemptRun<T> {
    readonly resource: string;
    readonly generation: number;
    readonly record: JsonObject;
    readonly controller: AbortController;
    readonly scope: ReturnType<typeof abortScope>;
    readonly completion: Promise<T>;
    readonly resolve: (value: T) => void;
    readonly reject: (error: unknown) => void;
    readonly background: boolean;
}

/** Coordinates intent in each manager's existing publication records. */
export abstract class StateUpdateCoordinator<T> {
    readonly #gates = new Map<string, AsyncGate>();
    readonly #active = new Map<string, AttemptRun<T>>();
    readonly #workers = new Set<Promise<unknown>>();
    readonly #retries = new Map<string, { generation: number; failures: number; at: number | null }>();
    readonly #wake = new AsyncPulse();
    #runtime: Promise<void> | undefined;
    protected abstract readonly operation: string;
    protected abstract readonly query: RecordQuery;
    protected readonly requestField: string = 'document';
    constructor(protected readonly store: MeshlineStore, protected readonly clock: RuntimeClock) {}
    protected abstract key(resource: string): RecordKey;
    protected abstract resource(row: StoredRecord): string;
    protected abstract create(resource: string, intent: JsonObject): JsonObject;
    protected abstract publish(attempt: StateUpdateAttempt<T>, signal: AbortSignal): Promise<T>;
    protected abstract report(resource: string, error: unknown): void;
    protected beforeMerge(_previous: JsonObject, _patch: JsonObject): void {}
    protected followUp(_result: T): number | null { return null; }
    protected retryAt(_row: JsonObject, error: unknown, failures: number): number | null {
        const delay = Math.max(Math.min(30, 2 ** Math.min(5, failures - 1)), error instanceof RelayError ? error.retryAfter ?? 0 : 0);
        return this.canRetry(error) ? Math.min(Number.MAX_SAFE_INTEGER, this.clock.nowSeconds() + delay) : null;
    }
    protected canRetry(error: unknown): boolean {
        if (error instanceof AggregateError) return error.errors.length > 0 && error.errors.every(cause => this.canRetry(cause));
        if (error instanceof RelayError) return ['stale_state', 'state_conflict', 'clock_skew', 'rate_limited', 'internal_error', 'bad_gateway', 'temporarily_unavailable', 'unauthorized', 'route_stale', 'target_not_local'].includes(error.code);
        if (error instanceof StateConflictError || error instanceof TypeError) return true;
        if (error instanceof Error && ['AbortError', 'TimeoutError', 'QuotaExceededError', 'UnknownError'].includes(error.name)) return true;
        if (error instanceof Error && 'code' in error && String(error.code).includes('SQLITE')) return true;
        if (error instanceof Error && error.cause !== undefined) return this.canRetry(error.cause);
        return false;
    }
    #gate(resource: string): AsyncGate {
        let gate = this.#gates.get(resource);
        if (!gate) { gate = new AsyncGate(); this.#gates.set(resource, gate); }
        return gate;
    }
    #error(resource: string): StateUpdateSupersededError { return new StateUpdateSupersededError(`${this.operation}:${resource}`); }
    async submit(resource: string, patch: JsonObject, signal: AbortSignal): Promise<T> {
        patch = copy(patch);
        const run = await this.#gate(resource).run(async () => {
            const record = await updateStore(this.store, [this.key(resource)], snapshot => {
                const previous = snapshot.sets[0]![0]?.value;
                const state = previous && publicationState(previous);
                const intent = copy(previous?.pending && state?.intent ? state.intent : {});
                this.beforeMerge(intent, patch); Object.assign(intent, copy(patch));
                const generation = (state?.generation ?? 0) + 1;
                if (!Number.isSafeInteger(generation)) throw new ProtocolError('revision_exhausted', 'Publication generation is exhausted.');
                const value = { ...(previous ?? this.create(resource, intent)), intent, generation, pending: true,
                    requestState: previous?.pending ? supersedeRequest(state!.requestState) : PublicationRequestState.None, nextAttemptAt: this.clock.nowSeconds() };
                return { mutations: [{ ...this.key(resource), kind: 'put', value }], result: value };
            }, signal);
            this.#retries.delete(resource);
            const previous = this.#active.get(resource);
            if (previous) { previous.reject(this.#error(resource)); previous.controller.abort(this.#error(resource)); }
            return this.#newRun(resource, record, signal, false);
        }, signal);
        this.#track(this.#execute(run));
        return awaitWithSignal(run.completion, signal);
    }
    async import(resource: string, intent: JsonObject, signal: AbortSignal): Promise<void> {
        await this.#gate(resource).run(() => updateStore(this.store, [this.key(resource)], snapshot => {
            const row = snapshot.sets[0]![0]?.value;
            if (!row?.pending || row.intent != null || row[this.requestField] == null) return { mutations: [], result: undefined };
            const generation = publicationState(row).generation + 1;
            if (!Number.isSafeInteger(generation)) throw new ProtocolError('revision_exhausted', 'Publication generation is exhausted.');
            return { mutations: [{ ...this.key(resource), kind: 'put', value: { ...row, intent: copy(intent), generation,
                requestState: PublicationRequestState.Prepared, nextAttemptAt: this.clock.nowSeconds() } }], result: undefined };
        }, signal), signal);
    }
    #newRun(resource: string, record: JsonObject, signal: AbortSignal, background: boolean): AttemptRun<T> {
        const controller = createAbortController();
        let resolve!: (value: T) => void; let reject!: (error: unknown) => void;
        const completion = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
        void completion.catch(() => {});
        const run = { resource, generation: publicationState(record).generation, record, controller, scope: abortScope([signal, controller.signal]), completion, resolve, reject, background };
        this.#active.set(resource, run); return run;
    }
    start(signal: AbortSignal): void { this.#runtime = this.#recover(signal); }
    async stop(): Promise<void> { await this.#runtime; this.#runtime = undefined; }
    async drain(): Promise<void> { while (this.#workers.size) await Promise.allSettled([...this.#workers]); }
    #track(work: Promise<unknown>): void {
        this.#workers.add(work);
        void work.then(() => this.#workers.delete(work), () => this.#workers.delete(work));
    }
    #scheduled(resource: string, row: JsonObject): number | null {
        const saved = publicationState(row); const retry = this.#retries.get(resource);
        return retry?.generation === saved.generation ? retry.at : saved.nextAttemptAt;
    }
    async #pending(signal: AbortSignal): Promise<readonly StoredRecord[]> {
        return (await this.store.read([this.query], signal)).sets[0]!.filter(row => row.value.pending === true && row.value.intent != null && this.#scheduled(this.resource(row), row.value) !== null);
    }
    async #recover(signal: AbortSignal): Promise<void> {
        try {
            for (;;) {
                try {
                    for (const row of await this.#pending(signal)) {
                        const resource = this.resource(row);
                        if (this.#scheduled(resource, row.value)! > this.clock.nowSeconds()) continue;
                        const run = await this.#gate(resource).run(async () => {
                            if (this.#active.has(resource)) return undefined;
                            const current = (await this.store.read([this.key(resource)], signal)).sets[0]![0]?.value;
                            if (!current?.pending || current.intent == null) return undefined;
                            const next = this.#scheduled(resource, current);
                            return next !== null && next <= this.clock.nowSeconds() ? this.#newRun(resource, current, signal, true) : undefined;
                        }, signal);
                        if (run) {
                            this.#track(this.#execute(run));
                            void run.completion.catch(error => { if (!signal.aborted && !(error instanceof StateUpdateSupersededError)) this.report(resource, error); });
                        }
                    }
                    const waiting = (await this.#pending(signal)).filter(row => !this.#active.has(this.resource(row)));
                    const next = Math.min(...waiting.map(row => this.#scheduled(this.resource(row), row.value)!));
                    if (next <= this.clock.nowSeconds()) continue;
                    const stopWait = createAbortController();
                    const wait = abortScope([signal, stopWait.signal]);
                    try {
                        await Promise.race([this.#wake.wait(wait.signal), ...(next === Infinity ? [] : [this.clock.delay(Math.min(60_000, (next - this.clock.nowSeconds()) * 1000), wait.signal)])]);
                    } finally { stopWait.abort(); wait.dispose(); }
                    throwIfAborted(signal);
                } catch (error) {
                    if (signal.aborted) throw error;
                    this.report(this.operation, error); await this.clock.delay(1000, signal);
                }
            }
        } catch (error) { if (!signal.aborted) throw error; }
        finally { await Promise.allSettled([...this.#active.values()].filter(run => run.background).map(run => run.completion)); }
    }
    async #execute(run: AttemptRun<T>): Promise<void> {
        const attempt = new StateUpdateAttempt(this, run.resource, run.generation, run.record, this.requestField, run.background);
        try {
            const work = this.publish(attempt, run.scope.signal); this.#track(work);
            const result = await awaitWithSignal(work, run.scope.signal);
            await this.current(run.resource, run.generation, async () => {
                await updateStore(this.store, [this.key(run.resource)], snapshot => {
                    const row = snapshot.sets[0]![0]!.value; const next = this.followUp(result); const pending = row.pending === true && next !== null;
                    if (!row.pending && row.intent == null && row.nextAttemptAt == null) return { mutations: [], result: undefined };
                    return { mutations: [{ ...this.key(run.resource), kind: 'put', value: { ...row, pending, intent: pending ? row.intent! : null, nextAttemptAt: pending ? next : null } }], result: undefined };
                });
                this.#retries.delete(run.resource); run.resolve(result);
            });
        } catch (error) {
            try {
                await this.current(run.resource, run.generation, async () => {
                    const row = (await this.store.read([this.key(run.resource)])).sets[0]![0]!.value;
                    if (!row.pending) return;
                    const previous = this.#retries.get(run.resource);
                    const failures = previous?.generation === run.generation ? Math.min(previous.failures + 1, 30) : 1;
                    const at = this.retryAt(row, run.scope.signal.aborted ? new DOMException('Publication canceled.', 'AbortError') : error, failures);
                    this.#retries.set(run.resource, { generation: run.generation, failures, at });
                    await updateStore(this.store, [this.key(run.resource)], snapshot => ({ mutations: [{ ...this.key(run.resource), kind: 'put', value: { ...snapshot.sets[0]![0]!.value, nextAttemptAt: at } }], result: undefined }));
                });
            } catch (storageError) { error = storageError instanceof StateUpdateSupersededError ? storageError : new AggregateError([error, storageError], 'Publication retry state could not be saved.'); }
            run.reject(error);
        } finally {
            if (this.#active.get(run.resource) === run) this.#active.delete(run.resource);
            run.scope.dispose(); this.#wake.pulse(); attempt.notify();
        }
    }
    async current<R>(resource: string, generation: number, action: () => Promise<R>, signal?: AbortSignal): Promise<R> {
        return this.#gate(resource).run(async () => {
            const row = (await this.store.read([this.key(resource)], signal)).sets[0]![0]?.value;
            if (!row || publicationState(row).generation !== generation) throw this.#error(resource);
            throwIfAborted(signal); return action();
        }, signal);
    }
    async saveRequest(resource: string, generation: number, request: JsonObject, fields: JsonObject, signal: AbortSignal): Promise<void> {
        await this.current(resource, generation, () => updateStore(this.store, [this.key(resource)], snapshot => ({ mutations: [{ ...this.key(resource), kind: 'put', value: {
            ...snapshot.sets[0]![0]!.value, ...fields, [this.requestField]: copy(request), pending: true, requestState: PublicationRequestState.Prepared,
        } }], result: undefined }), signal), signal);
    }
    async reject(resource: string, generation: number, request: JsonObject): Promise<void> {
        await this.current(resource, generation, () => updateStore(this.store, [this.key(resource)], snapshot => {
            const row = snapshot.sets[0]![0]!.value;
            return { mutations: canonicalJson(row[this.requestField] ?? null) === canonicalJson(request)
                ? [{ ...this.key(resource), kind: 'put', value: { ...row, requestState: PublicationRequestState.Rejected } }] : [], result: undefined };
        }));
    }
}

export class StateUpdateAttempt<T> {
    readonly intent: JsonObject;
    readonly #notifications: (() => void)[] = [];
    #completed = false;
    constructor(readonly owner: StateUpdateCoordinator<T>, readonly resource: string, readonly generation: number,
        readonly record: JsonObject, readonly requestField: string, readonly background: boolean) { this.intent = copy(publicationState(record).intent!); }
    deferNotification(action: () => void): void { if (!this.#completed) this.#notifications.push(action); }
    notify(): void { this.#completed = true; for (const action of this.#notifications.splice(0)) action(); }
    get request(): JsonObject | undefined { const request = this.record[this.requestField]; return publicationState(this.record).requestState === PublicationRequestState.Prepared && request != null ? requireObject(request) : undefined; }
    current<R>(action: () => Promise<R>, signal?: AbortSignal): Promise<R> { return this.owner.current(this.resource, this.generation, action, signal); }
    check(signal?: AbortSignal): Promise<void> { return this.current(() => Promise.resolve(), signal); }
    async saveRequest(request: JsonObject, fields: JsonObject, signal: AbortSignal): Promise<void> {
        await this.owner.saveRequest(this.resource, this.generation, request, fields, signal);
        Object.assign(this.record, fields, { [this.requestField]: copy(request), requestState: PublicationRequestState.Prepared });
    }
    async reject(request: JsonObject): Promise<void> {
        await this.owner.reject(this.resource, this.generation, request); this.record.requestState = PublicationRequestState.Rejected;
    }
}
