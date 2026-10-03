import * as sdk from '@meshline/sdk';

export class MessagingClock implements sdk.RuntimeClock {
    wall = 1730000000; elapsed = 0;
    nowSeconds(): number { return this.wall; }
    monotonicMilliseconds(): number { return this.elapsed; }
    readonly waiting = new Set<{ milliseconds: number; complete(): void }>();
    delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
        sdk.throwIfAborted(signal);
        return new Promise((resolve, reject) => {
            const cleanup = (): void => { this.waiting.delete(wait); signal?.removeEventListener('abort', abort); };
            const abort = (): void => { cleanup(); reject(signal!.reason); };
            const wait = { milliseconds, complete() { cleanup(); resolve(); } }; this.waiting.add(wait); signal?.addEventListener('abort', abort, { once: true });
        });
    }
    tick(): void { this.wall += 15; this.elapsed += 15000; for (const wait of [...this.waiting]) if (wait.milliseconds <= 30000) wait.complete(); }
}
