import { throwIfAborted } from './clock.js';
import { abortReason } from './abort.js';

/** One consumer, coalesced wakeups. Pulses received during processing remain pending. */
export class AsyncPulse {
    #pending = false;
    #wake: (() => void) | undefined;
    pulse(): void { this.#pending = true; this.#wake?.(); }
    async wait(signal: AbortSignal): Promise<void> {
        throwIfAborted(signal);
        if (this.#wake) throw new Error('AsyncPulse supports only one consumer.');
        if (!this.#pending) await new Promise<void>((resolve, reject) => {
            const cleanup = (): void => { this.#wake = undefined; signal.removeEventListener('abort', aborted); };
            const aborted = (): void => { cleanup(); reject(abortReason(signal)); };
            this.#wake = () => { cleanup(); resolve(); };
            signal.addEventListener('abort', aborted, { once: true });
        });
        throwIfAborted(signal); this.#pending = false;
    }
}
