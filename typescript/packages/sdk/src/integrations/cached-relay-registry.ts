import { ProtocolError } from '../errors.js';
import { validateRelayId } from '../identity/neo.js';
import type { RelayEntry, RelayRegistry } from '../interactions.js';
import { NetworkContext } from '../protocol/context.js';
import { systemClock, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { MetadataCache } from '../runtime/metadata-cache.js';
import { webEndpoint } from '../transport/endpoint.js';

/** Optional, caller-owned registry cache. Reads trigger refreshes; no polling timer is installed. */
export class CachedRelayRegistry implements RelayRegistry {
    readonly context: NetworkContext;
    readonly #directory: MetadataCache<readonly RelayEntry[]>;
    readonly #entries = new Map<string, MetadataCache<{ entry: RelayEntry | undefined }>>();
    readonly #known = new Map<string, RelayEntry>();
    readonly #absent = new Set<string>();
    readonly #changedAt = new Map<string, number>();
    #generation = 0;
    #disposed = false;
    constructor(readonly source: RelayRegistry, readonly clock: RuntimeClock = systemClock) {
        this.context = NetworkContext.parse(source.context.toString());
        this.#directory = new MetadataCache(clock);
    }
    #check(signal?: AbortSignal): void {
        throwIfAborted(signal);
        if (this.#disposed) throw new DOMException('Registry cache disposed.', 'AbortError');
        if (this.source.context.toString() !== this.context.toString()) throw new ProtocolError('context_changed', 'Registry context changed.');
    }
    #report(error: unknown): void { console.warn('Registry metadata refresh failed.', error); }
    #validate(entry: RelayEntry): void {
        validateRelayId(entry.relayId); webEndpoint(entry.endpoint, 'https:');
        if (!['active', 'disabled', 'suspended'].includes(entry.status) || typeof entry.updatedAt !== 'bigint' || entry.updatedAt < 0n || entry.updatedAt > 0xffffffffffffffffn)
            throw new ProtocolError('invalid_registry', 'Invalid registry entry.');
    }
    #superseded(id: string, observed: number): boolean { return this.#absent.has(id) && (this.#changedAt.get(id) ?? 0) > observed; }
    #version(entry: RelayEntry): void {
        if (entry.updatedAt < (this.#known.get(entry.relayId)?.updatedAt ?? 0n)) throw new ProtocolError('stale_registry', 'Registry returned an older registration.');
    }
    #accept(entry: RelayEntry, observed: number): void {
        const id = entry.relayId, previous = this.#known.get(id);
        if (this.#superseded(id, observed) || previous && (this.#changedAt.get(id) ?? 0) > observed && entry.updatedAt <= previous.updatedAt) return;
        this.#version(entry); this.#known.set(id, { ...entry }); this.#absent.delete(id); this.#changedAt.set(id, ++this.#generation);
    }
    #remove(id: string, observed: number): void {
        if ((this.#changedAt.get(id) ?? 0) > observed) return;
        this.#absent.add(id); this.#changedAt.set(id, ++this.#generation);
    }
    async getRelay(relayId: string, signal?: AbortSignal): Promise<RelayEntry | undefined> {
        this.#check(signal); validateRelayId(relayId);
        if (this.#directory.value?.some(entry => entry.relayId === relayId)) await this.#readDirectory(signal);
        else {
            let cache = this.#entries.get(relayId);
            if (!cache) { cache = new MetadataCache(this.clock); this.#entries.set(relayId, cache); }
            await cache.read(value => value.entry !== undefined, async token => {
                const observed = this.#generation;
                const entry = await this.source.getRelay(relayId, token); this.#check(token);
                if (entry) {
                    if (entry.relayId !== relayId) throw new ProtocolError('invalid_identity', 'Registry returned another relay.');
                    this.#validate(entry); this.#accept(entry, observed);
                } else this.#remove(relayId, observed);
                return { entry: this.#absent.has(relayId) ? undefined : this.#known.get(relayId) };
            }, error => this.#report(error), signal);
        }
        this.#check(signal);
        const entry = this.#absent.has(relayId) ? undefined : this.#known.get(relayId);
        return entry && { ...entry };
    }
    async #readDirectory(signal?: AbortSignal): Promise<void> {
        this.#check(signal);
        await this.#directory.read(() => true, async token => {
            const observed = this.#generation, snapshot = new Map<string, RelayEntry>();
            for await (const entry of this.source.getRelays(token)) {
                this.#validate(entry);
                if (snapshot.has(entry.relayId)) throw new ProtocolError('invalid_registry', 'Registry contains duplicate relays.');
                snapshot.set(entry.relayId, { ...entry });
            }
            this.#check(token);
            for (const entry of snapshot.values()) if (!this.#superseded(entry.relayId, observed)) this.#version(entry);
            for (const entry of snapshot.values()) this.#accept(entry, observed);
            for (const id of this.#known.keys()) if (!snapshot.has(id)) this.#remove(id, observed);
            return [...snapshot.values()];
        }, error => this.#report(error), signal);
    }
    async *getRelays(signal?: AbortSignal): AsyncIterable<RelayEntry> {
        await this.#readDirectory(signal);
        const snapshot = [...this.#known.values()].filter(entry => !this.#absent.has(entry.relayId));
        for (const entry of snapshot) { this.#check(signal); yield { ...entry }; }
    }
    /** Cancels and drains refreshes. The source registry remains owned by the caller. */
    async dispose(): Promise<void> {
        this.#disposed = true;
        await Promise.all([this.#directory.stop(true), ...[...this.#entries.values()].map(cache => cache.stop(true))]);
        this.#entries.clear(); this.#known.clear(); this.#absent.clear(); this.#changedAt.clear();
    }
}
