import { createAbortController } from '../runtime/abort.js';
import { ProtocolError } from '../errors.js';
import { validateRelayId } from '../identity/neo.js';
import type { RelayRegistry } from '../interactions.js';
import { relayDescriptorCodec, relayInfoCodec, validateRelayDescriptor, validateRelayInfo, type RelayDescriptor, type RelayInfo } from '../models/relay.js';
import { NetworkContext } from '../protocol/context.js';
import { awaitWithSignal, systemClock, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { webEndpoint } from './endpoint.js';
import type { HttpRelayTransport } from './http.js';

/** Resolves one trusted relay identity; descriptors, never old URLs, authorize endpoint selection. */
export class RelayDiscovery {
    readonly #lifetime = createAbortController();
    readonly #context: NetworkContext;
    #cached: RelayDescriptor | undefined;
    #pending: Promise<RelayDescriptor> | undefined;
    constructor(readonly relayId: string, readonly registry: RelayRegistry, readonly transport: HttpRelayTransport, readonly clock: RuntimeClock = systemClock) {
        validateRelayId(relayId);
        this.#context = NetworkContext.parse(registry.context.toString());
    }
    get context(): NetworkContext { return this.#context; }

    async getDescriptor(signal?: AbortSignal): Promise<RelayDescriptor> {
        throwIfAborted(signal); throwIfAborted(this.#lifetime.signal);
        if (this.registry.context.toString() !== this.#context.toString()) throw new ProtocolError('context_changed', 'The relay registry network context has changed.');
        if (this.#cached && this.#cached.expiresAt > this.clock.nowSeconds()) return this.#copy(this.#cached);
        if (!this.#pending) {
            const pending = this.#discover();
            this.#pending = pending;
            pending.then(value => { this.#cached = value; if (this.#pending === pending) this.#pending = undefined; },
                () => { if (this.#pending === pending) this.#pending = undefined; });
        }
        return this.#copy(await awaitWithSignal(this.#pending, signal));
    }

    async #discover(): Promise<RelayDescriptor> {
        const signal = this.#lifetime.signal;
        const entry = await this.registry.getRelay(this.relayId, signal);
        throwIfAborted(signal);
        if (this.registry.context.toString() !== this.#context.toString()) throw new ProtocolError('context_changed', 'The registry network context changed during discovery.');
        if (!entry || entry.relayId !== this.relayId || entry.status !== 'active') throw new ProtocolError('inactive_relay', 'The registry does not identify an active relay with the requested ID.');
        webEndpoint(entry.endpoint, 'https:');
        const wire = await this.transport.request(entry.endpoint, 'GET', 'relay.descriptor', undefined, undefined, signal);
        if (wire === undefined) throw new ProtocolError('missing_result', 'Relay discovery returned no descriptor.');
        const descriptor = relayDescriptorCodec.decode(wire);
        validateRelayDescriptor(descriptor, this.#context, this.clock.nowSeconds());
        if (descriptor.relayId !== this.relayId) throw new ProtocolError('invalid_identity', 'The descriptor does not identify the registered relay.');
        throwIfAborted(signal);
        return descriptor;
    }

    async getInfo(signal?: AbortSignal): Promise<RelayInfo> {
        const descriptor = await this.getDescriptor(signal);
        const endpoint = descriptor.endpoints.find(endpoint => endpoint.startsWith('https://'))!;
        const wire = await this.transport.request(endpoint, 'GET', 'relay.info', undefined, undefined, signal);
        if (wire === undefined) throw new ProtocolError('missing_result', 'Relay info returned no result.');
        const info = relayInfoCodec.decode(wire);
        validateRelayInfo(info, descriptor);
        return info;
    }

    #copy(value: RelayDescriptor): RelayDescriptor { return relayDescriptorCodec.decode(relayDescriptorCodec.encode(value)); }
    dispose(): void { this.#cached = undefined; this.#lifetime.abort(new DOMException('Relay discovery was disposed.', 'AbortError')); }
}
