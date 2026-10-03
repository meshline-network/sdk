import { throwIfAborted } from './clock.js';

/** Serializes state changes without poisoning subsequent operations after a failure. */
export class AsyncGate {
    private tail: Promise<void> = Promise.resolve();

    run<T>(operation: () => T | Promise<T>, signal?: AbortSignal): Promise<T> {
        const result = this.tail.then(() => {
            throwIfAborted(signal);
            return operation();
        });
        // Callers receive the original rejection; only the queue tail is recovered.
        this.tail = result.then(() => undefined, () => undefined);
        return result;
    }
}
