import { throwIfAborted, type RuntimeClock } from '../../packages/sdk/dist/runtime/clock.js';

export class AdvancingClock implements RuntimeClock {
    wall = 1730000000;
    elapsed = 0;
    readonly delays: number[] = [];
    nowSeconds(): number { return this.wall; }
    monotonicMilliseconds(): number { return this.elapsed; }
    async delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
        throwIfAborted(signal);
        this.delays.push(milliseconds);
        this.elapsed += milliseconds;
    }
}
