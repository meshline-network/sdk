import { createAbortController } from './abort.js';
import { awaitWithSignal, throwIfAborted, type RuntimeClock } from './clock.js';

type Result<T> = { value: T | undefined } | { error: unknown };

/** One object's refresh coordination. Owners retain validation, key selection and persistence. */
export class MetadataCache<T> {
    #value: T | undefined;
    #refreshAfter = -Infinity;
    #retryAfter = -Infinity;
    #pending: Promise<Result<T>> | undefined;
    #last: Result<T> | undefined;
    #lifetime = createAbortController();
    #stopping: Promise<void> | undefined;
    #disposed = false;
    #generation = 0;
    #freshness = 0;
    constructor(readonly clock: RuntimeClock, value?: T, refreshedAt?: number) {
        this.#value = value;
        if (refreshedAt !== undefined) this.#refreshAfter = refreshedAt + 3_600_000;
    }
    get value(): T | undefined { return this.#value; }
    publish(value: T, completed?: number): void {
        this.#value = value; this.#generation++; this.#retryAfter = -Infinity;
        if (completed !== undefined) this.recordSuccess(completed);
    }
    recordSuccess(completed: number): void { this.#refreshAfter = completed + 3_600_000; this.#freshness++; }
    invalidate(): void { this.#value = undefined; this.#generation++; }
    reject(error: unknown): void {
        this.#refreshAfter = this.#retryAfter = this.clock.monotonicMilliseconds() + 60_000;
        if (!this.#pending) this.#last = { error };
    }
    async drain(signal?: AbortSignal): Promise<void> {
        if (this.#pending) {
            const result = await awaitWithSignal(this.#pending, signal);
            if ('error' in result && result.error instanceof Error && result.error.name === 'AbortError') throw result.error;
        }
    }
    async read(usable: (value: T) => boolean, fetch: (signal: AbortSignal) => Promise<T | undefined>,
        report: (error: unknown) => void, signal?: AbortSignal, force = false): Promise<T | undefined> {
        throwIfAborted(signal);
        if (this.#disposed) throw new DOMException('Metadata cache disposed.', 'AbortError');
        const now = this.clock.monotonicMilliseconds();
        if (!force && this.#value !== undefined && usable(this.#value)) {
            if (!this.#stopping && now >= this.#refreshAfter) this.#start(usable, fetch, report);
            return this.#value;
        }
        if (this.#stopping) throw new DOMException('Metadata refreshes are stopping.', 'AbortError');
        const result = await awaitWithSignal(this.#pending ?? (now < this.#retryAfter && this.#last
            ? Promise.resolve(this.#last) : this.#start(usable, fetch, report)), signal);
        if ('error' in result) throw result.error;
        return result.value;
    }
    #start(usable: (value: T) => boolean, fetch: (signal: AbortSignal) => Promise<T | undefined>, report: (error: unknown) => void): Promise<Result<T>> {
        if (this.#pending) return this.#pending;
        const generation = this.#generation, freshness = this.#freshness, signal = this.#lifetime.signal;
        this.#retryAfter = -Infinity;
        const pending = Promise.resolve().then(async (): Promise<Result<T>> => {
            try {
                throwIfAborted(signal);
                let value: T | undefined = await fetch(signal); throwIfAborted(signal);
                if (generation !== this.#generation) value = this.#value;
                else if (value !== undefined) this.#value = value;
                if (freshness === this.#freshness) {
                    if (value === undefined || !usable(value)) this.#refreshAfter = this.#retryAfter = this.clock.monotonicMilliseconds() + 60_000;
                    else { this.#refreshAfter = this.clock.monotonicMilliseconds() + 3_600_000; this.#retryAfter = -Infinity; }
                }
                return { value };
            } catch (error) {
                if (!signal.aborted) {
                    if (freshness === this.#freshness) this.#refreshAfter = this.#retryAfter = this.clock.monotonicMilliseconds() + 60_000;
                    try { report(error); } catch (observer) { error = new AggregateError([error, observer], 'Metadata refresh and observer failed.'); }
                }
                return { error };
            }
        });
        this.#pending = pending;
        void pending.then(result => { this.#last = result; if (this.#pending === pending) this.#pending = undefined; });
        return pending;
    }
    stop(dispose = false): Promise<void> {
        if (this.#disposed) return this.#stopping ?? Promise.resolve();
        this.#disposed = dispose;
        if (this.#stopping) return this.#stopping;
        this.#lifetime.abort(new DOMException('Metadata refresh stopped.', 'AbortError'));
        return this.#stopping = Promise.resolve(this.#pending).then(() => {
            this.#pending = undefined; this.#last = undefined; this.#retryAfter = -Infinity;
            this.#lifetime = createAbortController(); this.#stopping = undefined;
            if (this.#disposed) this.#value = undefined;
        });
    }
}
