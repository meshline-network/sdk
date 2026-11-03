import { ProtocolError, StateConflictError } from '../errors.js';
import { canonicalJson, parseJson, requireObject, type JsonObject } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { AsyncPulse } from '../runtime/async-pulse.js';
import { abortReason, createAbortController } from '../runtime/abort.js';
import { abortScope, awaitWithSignal, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { isCurrentRequest, publicationState, PublicationRequestState, supersedeRequest } from '../storage/state-publication.js';
import type { MeshlineStore, RecordKey, RecordQuery, StoredRecord } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { RelayError } from '../transport/relay-error.js';
import { StateUpdateSupersededError } from './state-update-error.js';

const copy = (value: JsonObject): JsonObject => requireObject(parseJson(canonicalJson(value)));
function retryError(error: unknown, signal: AbortSignal): unknown {
    if (!signal.aborted) return error;
    const reason = abortReason(signal); const seen = new Set<unknown>();
    for (let cause = error; !seen.has(cause);) {
        if (cause === reason) return new DOMException('Publication canceled.', 'AbortError');
        seen.add(cause);
        if (!(cause instanceof Error) || cause.cause === undefined) break;
        cause = cause.cause;
    }
    return error;
}
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
    readonly waitForPublication: boolean;
    readonly settled: boolean;
}

export interface StateUpdateFailure { readonly error: unknown; readonly occurredAt: number; readonly rejectedRequest?: string }

/** Coordinates intent in each manager's existing publication records. */
export abstract class StateUpdateCoordinator<T> {
    readonly #gates = new Map<string, AsyncGate>();
    readonly #active = new Map<string, AttemptRun<T>>();
    readonly #workers = new Set<Promise<unknown>>();
    readonly #retries = new Map<string, { generation: number; failures: number; at: number | null; failure: StateUpdateFailure }>();
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
    protected setError(_row: JsonObject, _failure: StateUpdateFailure | undefined): void {}
    protected changed(_resource: string): void {}
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
    withPublicationLock<R>(resource: string, action: () => Promise<R>, signal?: AbortSignal): Promise<R> {
        return this.#gate(resource).run(action, signal);
    }
    #applyFailure(row: JsonObject, failure: StateUpdateFailure): void {
        if (failure.rejectedRequest !== undefined && canonicalJson(row[this.requestField] ?? null) === failure.rejectedRequest
            && isCurrentRequest(publicationState(row).requestState)) row.requestState = PublicationRequestState.Rejected;
        this.setError(row, failure);
    }
    #restoreFailure(resource: string, row: JsonObject | undefined): void {
        const retry = this.#retries.get(resource);
        if (row?.pending && retry?.generation === publicationState(row).generation) this.#applyFailure(row, retry!.failure);
    }
    read<R>(resource: string, project: (row: JsonObject | undefined, publishing: boolean, next: number | null) => R, signal: AbortSignal): Promise<R> {
        return this.#gate(resource).run(async () => {
            const row = (await this.store.read([this.key(resource)], signal)).sets[0]![0]?.value;
            const generation = row && publicationState(row).generation;
            const run = this.#active.get(resource);
            this.#restoreFailure(resource, row);
            return project(row, !!row?.pending && run?.generation === generation && !run!.settled, row ? this.#scheduled(resource, row) : null);
        }, signal);
    }
    submit(resource: string, patch: JsonObject, signal: AbortSignal): Promise<T> { return this.#submit(resource, patch, signal, false); }
    // Initialization adopts current intent and drains an owned attempt on cancellation.
    // Joining an existing attempt leaves cancellation ownership with its caller.
    resume(resource: string, initial: JsonObject, signal: AbortSignal): Promise<T> { return this.#submit(resource, initial, signal, true); }
    async #submit(resource: string, patch: JsonObject, signal: AbortSignal, resume: boolean): Promise<T> {
        patch = copy(patch);
        const { run, ownsRun } = await this.#gate(resource).run(async () => {
            const existing = this.#active.get(resource);
            let joined = false;
            const record = await updateStore(this.store, [this.key(resource)], snapshot => {
                const previous = snapshot.sets[0]![0]?.value;
                this.#restoreFailure(resource, previous);
                const state = previous && publicationState(previous);
                const retain = resume && previous?.pending === true && state?.intent !== undefined;
                joined = !!retain && existing?.generation === state!.generation && !existing!.settled;
                if (joined) return { mutations: [], result: previous! };
                if (retain) {
                    const value = { ...previous, nextAttemptAt: this.clock.nowSeconds() };
                    return { mutations: [{ ...this.key(resource), kind: 'put', value }], result: value };
                }
                const intent = copy(previous?.pending && state?.intent ? state.intent : {});
                const incoming = copy(patch);
                this.beforeMerge(intent, incoming); Object.assign(intent, incoming);
                const generation = (state?.generation ?? 0) + 1;
                if (!Number.isSafeInteger(generation)) throw new ProtocolError('revision_exhausted', 'Publication generation is exhausted.');
                const value = { ...(previous ?? this.create(resource, intent)), intent, generation, pending: true,
                    requestState: previous?.pending ? supersedeRequest(state!.requestState) : PublicationRequestState.None, nextAttemptAt: this.clock.nowSeconds() };
                this.setError(value, undefined);
                return { mutations: [{ ...this.key(resource), kind: 'put', value }], result: value };
            }, signal);
            if (joined) return { run: existing!, ownsRun: false };
            if (!resume) this.#retries.delete(resource);
            const previous = this.#active.get(resource);
            if (previous) {
                previous.reject(this.#error(resource));
                previous.controller.abort(this.#error(resource));
            }
            return { run: this.#newRun(resource, record, signal, false, resume), ownsRun: true };
        }, signal);
        if (ownsRun) this.#track(this.#execute(run));
        return ownsRun && resume ? run.completion : awaitWithSignal(run.completion, signal);
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
    #newRun(resource: string, record: JsonObject, signal: AbortSignal, background: boolean, waitForPublication = false): AttemptRun<T> {
        this.#restoreFailure(resource, record);
        const controller = createAbortController();
        let resolve!: (value: T) => void; let reject!: (error: unknown) => void;
        const completion = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
        void completion.catch(() => {});
        let settled = false;
        const run = { resource, generation: publicationState(record).generation, record, controller, scope: abortScope([signal, controller.signal]), completion,
            resolve: (value: T) => { settled = true; resolve(value); }, reject: (error: unknown) => { settled = true; reject(error); },
            background, waitForPublication, get settled() { return settled; } };
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
            this.changed(run.resource);
            const work = this.publish(attempt, run.scope.signal); this.#track(work);
            const result = await (run.waitForPublication ? work : awaitWithSignal(work, run.scope.signal));
            await this.current(run.resource, run.generation, async () => {
                await updateStore(this.store, [this.key(run.resource)], snapshot => {
                    const row = snapshot.sets[0]![0]!.value; const next = this.followUp(result); const pending = row.pending === true && next !== null;
                    if (!row.pending && row.intent == null && row.nextAttemptAt == null) return { mutations: [], result: undefined };
                    const value = { ...row, pending, intent: pending ? row.intent! : null, nextAttemptAt: pending ? next : null };
                    this.setError(value, undefined);
                    return { mutations: [{ ...this.key(run.resource), kind: 'put', value }], result: undefined };
                });
                this.#retries.delete(run.resource); run.resolve(result);
            });
        } catch (error) {
            const failure = { error, occurredAt: this.clock.nowSeconds(), ...(attempt.rejectedRequest === undefined ? {} : { rejectedRequest: attempt.rejectedRequest }) };
            try {
                await this.#saveFailure(run, failure);
            } catch (storageError) { error = storageError instanceof StateUpdateSupersededError ? storageError : new AggregateError([error, storageError], 'Publication retry state could not be saved.'); }
            run.reject(error);
        } finally {
            if (this.#active.get(run.resource) === run) { this.#active.delete(run.resource); this.changed(run.resource); }
            run.scope.dispose(); this.#wake.pulse(); attempt.notify();
        }
    }
    #saveFailure(run: AttemptRun<T>, failure: StateUpdateFailure): Promise<void> {
        return this.#gate(run.resource).run(async () => {
            if (this.#active.get(run.resource) !== run) throw this.#error(run.resource);
            const previous = this.#retries.get(run.resource);
            const failures = previous?.generation === run.generation ? Math.min(previous.failures + 1, 30) : 1;
            const error = retryError(failure.error, run.scope.signal);
            const remember = (row: JsonObject): number | null => {
                const at = this.retryAt(row, error, failures);
                this.#retries.set(run.resource, { generation: run.generation, failures, at, failure });
                return at;
            };
            // Retain diagnostics before reading storage; durable generation and pending
            // checks keep a discovery-retired fallback out of public state and recovery.
            remember(run.record);
            await updateStore(this.store, [this.key(run.resource)], snapshot => {
                const row = snapshot.sets[0]![0]?.value;
                if (!row || publicationState(row).generation !== run.generation) {
                    this.#retries.delete(run.resource); throw this.#error(run.resource);
                }
                if (!row.pending) { this.#retries.delete(run.resource); return { mutations: [], result: undefined }; }
                const value = { ...row, nextAttemptAt: remember(row) };
                this.#applyFailure(value, failure);
                return { mutations: [{ ...this.key(run.resource), kind: 'put', value }], result: undefined };
            });
        });
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
    #rejectedRequest: string | undefined;
    readonly #notifications: (() => void)[] = [];
    #completed = false;
    constructor(readonly owner: StateUpdateCoordinator<T>, readonly resource: string, readonly generation: number,
        readonly record: JsonObject, readonly requestField: string, readonly background: boolean) {
        const state = publicationState(record); this.intent = copy(state.intent!);
        if ((state.requestState === PublicationRequestState.Rejected || state.requestState === PublicationRequestState.SupersededRejected) && record[requestField] != null)
            this.#rejectedRequest = canonicalJson(record[requestField]!);
    }
    deferNotification(action: () => void): void { if (!this.#completed) this.#notifications.push(action); }
    notify(): void { this.#completed = true; for (const action of this.#notifications.splice(0)) action(); }
    get request(): JsonObject | undefined { const request = this.record[this.requestField]; return publicationState(this.record).requestState === PublicationRequestState.Prepared && request != null ? requireObject(request) : undefined; }
    get rejectedRequest(): string | undefined { return this.#rejectedRequest; }
    // Persist a known rejection with diagnostics and its retry deadline.
    recordRejection(request: JsonObject): void { this.#rejectedRequest = canonicalJson(request); }
    current<R>(action: () => Promise<R>, signal?: AbortSignal): Promise<R> { return this.owner.current(this.resource, this.generation, action, signal); }
    check(signal?: AbortSignal): Promise<void> { return this.current(() => Promise.resolve(), signal); }
    async saveRequest(request: JsonObject, fields: JsonObject, signal: AbortSignal): Promise<void> {
        await this.owner.saveRequest(this.resource, this.generation, request, fields, signal);
        Object.assign(this.record, fields, { [this.requestField]: copy(request), requestState: PublicationRequestState.Prepared });
        this.#rejectedRequest = undefined;
    }
    async reject(request: JsonObject): Promise<void> {
        await this.owner.reject(this.resource, this.generation, request); this.record.requestState = PublicationRequestState.Rejected;
    }
}
