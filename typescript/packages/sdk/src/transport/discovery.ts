import { ProtocolError } from '../errors.js';
import { validateRelayId } from '../identity/neo.js';
import type { RelayEntry, RelayRegistry } from '../interactions.js';
import { relayDescriptorCodec, relayInfoCodec, relayLimitsCodec, validateRelayDescriptor, validateRelayInfo, type RelayDescriptor, type RelayInfo, type RelayLimits } from '../models/relay.js';
import { NetworkContext } from '../protocol/context.js';
import { abortScope, awaitWithSignal, systemClock, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { MetadataCache } from '../runtime/metadata-cache.js';
import { webEndpoint } from './endpoint.js';
import type { HttpRelayTransport } from './http.js';

// Pool sharing is internal; constructing a standalone RelayClient still owns its discovery.
const clients = new WeakMap<object, { discovery: RelayDiscovery; lifetime: AbortSignal | undefined }>();
const limitsReaders = new WeakMap<RelayDiscovery, (signal?: AbortSignal) => Promise<RelayLimits>>();
export function bindDiscovery(client: object, discovery: RelayDiscovery, lifetime?: AbortSignal): void {
    clients.set(client, { discovery, lifetime: lifetime ?? clients.get(client)?.lifetime });
}
export async function readDiscovery<T>(client: object, read: (discovery: RelayDiscovery, signal: AbortSignal) => Promise<T>, signal?: AbortSignal): Promise<T> {
    const binding = clients.get(client)!;
    const scope = abortScope([signal, binding.lifetime]);
    try { throwIfAborted(scope.signal); return await awaitWithSignal(read(binding.discovery, scope.signal), scope.signal); }
    finally { scope.dispose(); }
}
export function relayLimits(client: object, signal?: AbortSignal): Promise<RelayLimits> {
    return readDiscovery(client, (discovery, token) => limitsReaders.get(discovery)!(token), signal);
}

/** Verified relay metadata, isolated to one provider, network and relay identity. */
export class RelayDiscovery {
    readonly #context: NetworkContext;
    readonly #descriptor: MetadataCache<RelayDescriptor>;
    readonly #limits: MetadataCache<RelayLimits>;
    #infoGeneration = 0;
    #registration: RelayEntry | undefined;
    constructor(readonly relayId: string, readonly registry: RelayRegistry, readonly transport: HttpRelayTransport, readonly clock: RuntimeClock = systemClock) {
        validateRelayId(relayId); this.#context = NetworkContext.parse(registry.context.toString());
        this.#descriptor = new MetadataCache(clock); this.#limits = new MetadataCache(clock);
        limitsReaders.set(this, signal => this.#getLimits(signal));
    }
    get context(): NetworkContext { return this.#context; }
    #report(error: unknown): void { console.warn('Relay metadata refresh failed.', error); }
    async #entry(signal?: AbortSignal): Promise<RelayEntry> {
        throwIfAborted(signal);
        if (this.registry.context.toString() !== this.#context.toString()) throw new ProtocolError('context_changed', 'Registry network context changed.');
        const entry = await this.registry.getRelay(this.relayId, signal); throwIfAborted(signal);
        if (this.registry.context.toString() !== this.#context.toString()) throw new ProtocolError('context_changed', 'Registry network context changed during discovery.');
        if (!entry || entry.relayId !== this.relayId || entry.status !== 'active') {
            this.#descriptor.invalidate(); this.#limits.invalidate();
            throw new ProtocolError('inactive_relay', 'Registry does not identify an active relay.');
        }
        webEndpoint(entry.endpoint, 'https:');
        if (this.#registration && this.#registration.endpoint !== entry.endpoint) { this.#descriptor.invalidate(); this.#limits.invalidate(); }
        this.#registration = { ...entry }; return entry;
    }
    async getDescriptor(signal?: AbortSignal): Promise<RelayDescriptor> {
        await this.#entry(signal);
        const value = await this.#descriptor.read(value => value.expiresAt > this.clock.nowSeconds(), async token => {
            const entry = await this.#entry(token);
            const wire = await this.transport.request(entry.endpoint, 'GET', 'relay.descriptor', undefined, undefined, token);
            if (wire === undefined) throw new ProtocolError('missing_result', 'Relay discovery returned no descriptor.');
            const descriptor = relayDescriptorCodec.decode(wire); validateRelayDescriptor(descriptor, this.#context, this.clock.nowSeconds());
            if (descriptor.relayId !== this.relayId) throw new ProtocolError('invalid_identity', 'Descriptor belongs to another relay.');
            if ((await this.#entry(token)).endpoint !== entry.endpoint) throw new ProtocolError('registry_changed', 'Registration changed during discovery.');
            return descriptor;
        }, error => this.#report(error), signal);
        if (!value || value.expiresAt <= this.clock.nowSeconds()) throw new ProtocolError('expired_descriptor', 'Relay descriptor is unavailable or expired.');
        return relayDescriptorCodec.decode(relayDescriptorCodec.encode(value));
    }
    async getInfo(signal?: AbortSignal): Promise<RelayInfo> {
        const generation = ++this.#infoGeneration;
        const descriptor = await this.getDescriptor(signal);
        const wire = await this.transport.request(descriptor.endpoints.find(endpoint => endpoint.startsWith('https://'))!, 'GET', 'relay.info', undefined, undefined, signal);
        if (wire === undefined) throw new ProtocolError('missing_result', 'Relay info returned no result.');
        const info = relayInfoCodec.decode(wire); validateRelayInfo(info, descriptor);
        await this.#entry(signal);
        if (generation === this.#infoGeneration) this.#limits.publish(relayLimitsCodec.decode(relayLimitsCodec.encode(info.limits)), this.clock.monotonicMilliseconds());
        return info;
    }
    async #getLimits(signal?: AbortSignal): Promise<RelayLimits> {
        const descriptor = await this.getDescriptor(signal);
        const valid = (limits: RelayLimits): boolean => {
            try { validateRelayInfo({ relayId: this.relayId, name: 'Relay', serverTime: this.clock.nowSeconds(), limits }, descriptor); return true; }
            catch (error) { if (error instanceof ProtocolError) return false; throw error; }
        };
        const limits = await this.#limits.read(valid, async token => (await this.getInfo(token)).limits, error => this.#report(error), signal);
        if (!limits || !valid(limits)) throw new ProtocolError('missing_limits', 'Relay operational limits are unavailable.');
        return relayLimitsCodec.decode(relayLimitsCodec.encode(limits));
    }
    async dispose(): Promise<void> { await Promise.all([this.#descriptor.stop(true), this.#limits.stop(true)]); }
}
