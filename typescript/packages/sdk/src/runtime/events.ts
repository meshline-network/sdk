export type EventListener<T> = (value: T) => void | Promise<void>;

/** Typed subscriptions with awaited dispatch or nonblocking notifications. */
export class EventHub<Events extends object> {
    readonly #listeners = new Map<keyof Events, Set<EventListener<never>>>();
    on<K extends keyof Events>(event: K, listener: EventListener<Events[K]>): () => void {
        let listeners = this.#listeners.get(event);
        if (!listeners) { listeners = new Set(); this.#listeners.set(event, listeners); }
        listeners.add(listener as EventListener<never>);
        return () => { listeners.delete(listener as EventListener<never>); };
    }
    async emit<K extends keyof Events>(event: K, value: Events[K]): Promise<void> {
        const failures: unknown[] = [];
        for (const listener of [...(this.#listeners.get(event) ?? [])]) {
            try { await listener(value as never); }
            catch (error) { failures.push(error); }
        }
        if (failures.length !== 0) throw new AggregateError(failures, `A ${String(event)} event handler failed.`);
    }
    /** Invoke every subscriber immediately; application async work never delays other subscribers or the publisher. */
    notify<K extends keyof Events>(event: K, value: Events[K], failed: (error: AggregateError) => void): void {
        const failures: unknown[] = [];
        const report = (errors: unknown[]): void => { if (errors.length) failed(new AggregateError(errors, `A ${String(event)} event handler failed.`)); };
        for (const listener of [...(this.#listeners.get(event) ?? [])]) {
            try { const result = listener(value as never); if (result) void Promise.resolve(result).catch(error => report([error])); }
            catch (error) { failures.push(error); }
        }
        report(failures);
    }
    clear(): void { this.#listeners.clear(); }
}
