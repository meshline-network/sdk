import { awaitWithSignal, throwIfAborted } from './clock.js';

/** Serializes state changes without poisoning subsequent operations after a failure. */
export class AsyncGate {
    private tail: Promise<void> = Promise.resolve();

    run<T>(operation: () => T | Promise<T>, signal?: AbortSignal): Promise<T> {
        const previous = this.tail;
        const result = (async () => {
            await awaitWithSignal(previous, signal);
            throwIfAborted(signal);
            return operation();
        })();
        // Cancel queued callers promptly, but keep later work behind the active operation.
        // Once started, an operation must finish before its caller and the queue are released.
        const settled = result.then(() => undefined, () => undefined);
        this.tail = previous.then(() => settled);
        return result;
    }
}
