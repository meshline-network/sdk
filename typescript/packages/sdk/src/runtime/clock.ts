import { abortReason, createAbortController } from './abort.js';

/** Wall time signs timestamps; monotonic time measures session and backoff lifetimes. */
export interface RuntimeClock {
    nowSeconds(): number;
    monotonicMilliseconds(): number;
    delay(milliseconds: number, signal?: AbortSignal): Promise<void>;
}

export function throwIfAborted(signal?: AbortSignal): void {
    if (signal?.aborted) throw abortReason(signal);
}

export const systemClock: RuntimeClock = {
    nowSeconds: () => Math.floor(Date.now() / 1000),
    monotonicMilliseconds: () => performance.now(),
    delay(milliseconds, signal) {
        throwIfAborted(signal);
        if (!Number.isFinite(milliseconds) || milliseconds < 0 || milliseconds > 60_000)
            throw new RangeError('An individual delay must be between zero and 60000 milliseconds.');
        return new Promise<void>((resolve, reject) => {
            const onAbort = (): void => {
                clearTimeout(timer);
                signal?.removeEventListener('abort', onAbort);
                reject(abortReason(signal!));
            };
            const timer = setTimeout(() => { signal?.removeEventListener('abort', onAbort); resolve(); }, milliseconds);
            signal?.addEventListener('abort', onAbort, { once: true });
        });
    },
};

/** A caller can abandon a shared operation without canceling other callers' work. */
export function awaitWithSignal<T>(promise: Promise<T>, signal?: AbortSignal): Promise<T> {
    throwIfAborted(signal);
    if (!signal) return promise;
    return new Promise<T>((resolve, reject) => {
        const onAbort = (): void => { signal.removeEventListener('abort', onAbort); reject(abortReason(signal)); };
        signal.addEventListener('abort', onAbort, { once: true });
        promise.then(value => { signal.removeEventListener('abort', onAbort); resolve(value); }, error => {
            signal.removeEventListener('abort', onAbort); reject(error);
        });
    });
}

/** Explicit cleanup avoids accumulating listeners or request deadline timers. */
export function abortScope(signals: readonly (AbortSignal | undefined)[], timeoutMilliseconds?: number): { signal: AbortSignal; dispose(): void } {
    const controller = createAbortController();
    const listeners = new Map<AbortSignal, () => void>();
    for (const signal of signals) {
        if (!signal || listeners.has(signal)) continue;
        const onAbort = (): void => controller.abort(abortReason(signal));
        if (signal.aborted) onAbort();
        else { signal.addEventListener('abort', onAbort, { once: true }); listeners.set(signal, onAbort); }
    }
    const timer = timeoutMilliseconds === undefined ? undefined : setTimeout(() => {
        controller.abort(new DOMException('The relay request timed out.', 'TimeoutError'));
    }, timeoutMilliseconds);
    return {
        signal: controller.signal,
        dispose() { clearTimeout(timer); for (const [signal, listener] of listeners) signal.removeEventListener('abort', listener); listeners.clear(); },
    };
}
