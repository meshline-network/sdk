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
export function abortScope(signals: readonly (AbortSignal | undefined)[], timeoutMilliseconds?: number, operation = 'request'): { signal: AbortSignal; normalizeError(error: unknown): unknown; dispose(): void } {
    const controller = createAbortController();
    const listeners = new Map<AbortSignal, () => void>();
    for (const signal of signals) {
        if (!signal || listeners.has(signal)) continue;
        const onAbort = (): void => controller.abort(abortReason(signal));
        if (signal.aborted) onAbort();
        else { signal.addEventListener('abort', onAbort, { once: true }); listeners.set(signal, onAbort); }
    }
    let timeoutReason: Error | undefined;
    const timer = timeoutMilliseconds === undefined ? undefined : setTimeout(() => {
        if (controller.signal.aborted) return;
        timeoutReason = Object.assign(new DOMException(`Request '${operation}' timed out after ${timeoutMilliseconds} milliseconds.`, 'TimeoutError'), { operation, timeoutMilliseconds });
        controller.abort(timeoutReason);
    }, timeoutMilliseconds);
    return {
        signal: controller.signal,
        normalizeError(error: unknown): unknown {
            if (!controller.signal.aborted || !(error instanceof Error) || !['AbortError', 'TimeoutError'].includes(error.name)) return error;
            const reason: unknown = abortReason(controller.signal);
            if (reason instanceof Error && reason === timeoutReason && reason !== error && !('cause' in reason))
                Object.defineProperty(reason, 'cause', { value: error, configurable: true });
            return reason;
        },
        dispose() { clearTimeout(timer); for (const [signal, listener] of listeners) signal.removeEventListener('abort', listener); listeners.clear(); },
    };
}
