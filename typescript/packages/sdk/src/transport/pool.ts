import { createAbortController } from '../runtime/abort.js';
import { ProtocolError } from '../errors.js';
import { validateAccountId, validateRelayId } from '../identity/neo.js';
import type { RelayRegistry } from '../interactions.js';
import { certificateId } from '../models/identity.js';
import type { NetworkContext } from '../protocol/context.js';
import { awaitWithSignal, throwIfAborted } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import { RelayClient, type RelayTransportOptions } from './client.js';
import { RelayAuthenticator, type AuthenticationIdentity } from './session.js';

export interface RelayClientPoolOptions extends RelayTransportOptions { readonly context: NetworkContext; readonly accountId: string }
export interface RelayPoolEvents { readonly poolChanged: readonly RelayClient[]; readonly relayChanged: RelayClient; readonly errorOccurred: unknown }
interface Entry { client: RelayClient; ready: Promise<void>; mode: 'account' | 'device' | undefined; retired: boolean }

/** Owns account/device sessions separately; a pool binds to one network, account and device. */
export class RelayClientPool {
    readonly #events = new EventHub<RelayPoolEvents>();
    readonly #entries = new Map<string, Entry[]>();
    readonly #retirements = new Set<Promise<void>>();
    readonly #lifetime = createAbortController();
    readonly #options: RelayClientPoolOptions;
    #deviceId: string | undefined;
    #disposal: Promise<void> | undefined;
    #lastError: unknown;
    readonly context: NetworkContext;
    readonly accountId: string;
    constructor(options: RelayClientPoolOptions, readonly registry: RelayRegistry) {
        validateAccountId(options.accountId);
        if (options.context.toString() !== registry.context.toString()) throw new ProtocolError('invalid_context', 'Registry and client network contexts differ.');
        this.context = options.context; this.accountId = options.accountId; this.#options = { ...options };
    }
    get clients(): readonly RelayClient[] { return Object.freeze([...this.#entries.values()].flatMap(entries => entries.map(entry => entry.client))); }
    /** Last application observer failure; transport failures remain on each relay client's state. */
    get lastError(): unknown { return this.#lastError; }
    on<K extends keyof RelayPoolEvents>(event: K, listener: EventListener<RelayPoolEvents[K]>): () => void { return this.#events.on(event, listener); }
    #notify<K extends Exclude<keyof RelayPoolEvents, 'errorOccurred'>>(event: K, value: RelayPoolEvents[K]): void {
        this.#events.notify(event, value, error => {
            this.#lastError = error;
            this.#events.notify('errorOccurred', error, observerError => {
                this.#lastError = new AggregateError([error, observerError], 'Pool observer and error observer failed.');
            });
        });
    }

    async get(relayId: string, identity?: AuthenticationIdentity, signal?: AbortSignal): Promise<RelayClient> {
        throwIfAborted(signal); throwIfAborted(this.#lifetime.signal); validateRelayId(relayId);
        if (this.registry.context.toString() !== this.context.toString()) throw new ProtocolError('context_changed', 'Registry network context changed.');
        if (identity) {
            const account = identity.mode === 'account' ? identity.signer.accountId : identity.signer.certificate.account;
            if (account !== this.accountId) throw new ProtocolError('invalid_identity', 'Signer belongs to another account.');
            if (identity.mode === 'device') {
                const device = certificateId(identity.signer.certificate, this.context);
                if (this.#deviceId !== undefined && this.#deviceId !== device) throw new ProtocolError('invalid_identity', 'Pool is already bound to another device.');
                this.#deviceId = device;
            }
        }
        let entries = this.#entries.get(relayId);
        let entry = identity ? entries?.find(entry => entry.mode === identity.mode) : entries?.[0];
        if (!entry) {
            const authenticator = identity === undefined ? undefined : new RelayAuthenticator(relayId, this.context, identity, this.#options.clock);
            const client = new RelayClient(relayId, this.registry, this.#options, authenticator);
            entry = { client, mode: identity?.mode, retired: false, ready: Promise.resolve() };
            if (!entries) { entries = []; this.#entries.set(relayId, entries); }
            entries.push(entry);
            const captured = entry;
            client.on('stateChanged', () => this.#notify('relayChanged', client));
            client.on('faulted', () => { this.#retire(captured); });
            // Publish only after the readiness promise is installed, so a callback
            // acquiring the same relay waits for this authentication too.
            entry.ready = Promise.resolve().then(async () => {
                this.#notify('poolChanged', this.clients);
                if (authenticator) await client.authenticate(this.#lifetime.signal);
            });
            entry.ready.then(() => undefined, () => { this.#retire(captured); });
        }
        await awaitWithSignal(entry.ready, signal);
        throwIfAborted(this.#lifetime.signal); throwIfAborted(signal);
        if (entry.retired) throw new ProtocolError('retired_client', 'Relay client was retired while being acquired.');
        return entry.client;
    }

    #retire(entry: Entry): void {
        if (entry.retired) return;
        entry.retired = true;
        const entries = this.#entries.get(entry.client.relayId)!;
        entries.splice(entries.indexOf(entry), 1);
        if (entries.length === 0) this.#entries.delete(entry.client.relayId);
        const cleanup = (async () => { await entry.client.dispose(); this.#notify('poolChanged', this.clients); })();
        this.#retirements.add(cleanup);
        // Keep failed retirements for dispose/invalidate to surface; successful ones need no retained task.
        cleanup.then(() => this.#retirements.delete(cleanup), () => undefined);
    }

    /** Device registration/key changes require new device sessions; account sessions remain independent. */
    async invalidateDevice(): Promise<void> {
        throwIfAborted(this.#lifetime.signal);
        for (const entries of [...this.#entries.values()]) for (const entry of [...entries]) if (entry.mode === 'device') this.#retire(entry);
        this.#deviceId = undefined;
        await this.#finishRetirements();
    }
    async #finishRetirements(): Promise<void> {
        const jobs = [...this.#retirements]; const results = await Promise.allSettled(jobs);
        for (const job of jobs) this.#retirements.delete(job);
        const errors = results.filter((result): result is PromiseRejectedResult => result.status === 'rejected').map(result => result.reason);
        if (errors.length) throw new AggregateError(errors, 'Relay client retirement failed.');
    }
    dispose(): Promise<void> {
        return this.#disposal ??= (async () => {
            this.#lifetime.abort(new DOMException('Relay client pool was disposed.', 'AbortError'));
            for (const entries of [...this.#entries.values()]) for (const entry of [...entries]) this.#retire(entry);
            try { await this.#finishRetirements(); } finally { this.#events.clear(); }
        })();
    }
}
