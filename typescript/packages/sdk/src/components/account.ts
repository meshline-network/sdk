import { ProtocolError, StateConflictError } from '../errors.js';
import { matchesAccount, validateAccountId, validateRelayId, verifyRelay } from '../identity/neo.js';
import type { AccountSigner } from '../interactions.js';
import { accountRouteCodec, compareRoutes, routeAccountInput, routeRelayInput, validateRoute, type AccountRoute } from '../models/identity.js';
import { nextRevision } from '../protocol/context.js';
import { canonicalJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { throwIfAborted } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import type { MeshlineStore, RecordKey, StoreMutation } from '../storage/store.js';
import { finishSignedRequest, putSignedRequest, readSignedRequest, requestKey, type SignedRequest } from '../storage/signed-request.js';
import { updateStore } from '../storage/transaction.js';
import type { RelayClientPool } from '../transport/pool.js';
import { RelayError } from '../transport/relay-error.js';
import { ClientComponent, type BackgroundFailure, type ClientOptions } from './component.js';

export type AccountState = 'unknown' | 'notEstablished' | 'established';
export interface AccountSnapshot { readonly state: AccountState; readonly route: AccountRoute | undefined }
export interface AccountEvents { readonly accountChanged: AccountSnapshot }
export interface AccountManagerOptions extends ClientOptions { readonly store: MeshlineStore; readonly relayClients: RelayClientPool; readonly accountSigner?: AccountSigner }
export interface PublishRouteOptions { readonly validitySeconds: number; readonly revision?: number; readonly recovery?: boolean; readonly signal?: AbortSignal }
const method = 'account.route.publish';
const routeKey = (account: string): RecordKey => ({ collection: 'account_routes', key: account });
const clone = (route: AccountRoute): AccountRoute => accountRouteCodec.decode(accountRouteCodec.encode(route));
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);

/** Signed route discovery and durable, explicit publication retries. */
export class AccountManager extends ClientComponent {
    readonly #store: MeshlineStore;
    readonly #pool: RelayClientPool;
    readonly #signer: AccountSigner | undefined;
    readonly #gate = new AsyncGate();
    readonly #events = new EventHub<AccountEvents>();
    #state: AccountState = 'unknown';
    #route: AccountRoute | undefined;

    constructor(options: AccountManagerOptions) {
        super(options);
        if (options.relayClients.accountId !== this.accountId || options.relayClients.context.toString() !== this.context.toString())
            throw new ProtocolError('invalid_context', 'Relay pool belongs to another account or network.');
        this.#store = options.store; this.#pool = options.relayClients; this.#signer = options.accountSigner;
    }
    get state(): AccountState { return this.#state; }
    get route(): AccountRoute | undefined { return this.#route && clone(this.#route); }
    on<K extends keyof AccountEvents>(event: K, listener: EventListener<AccountEvents[K]>): () => void { return this.#events.on(event, listener); }
    protected override async onInitialize(signal: AbortSignal): Promise<void> {
        await this.#store.initialize({ context: this.context.toString(), accountId: this.accountId }, signal);
        const value = (await this.#store.read([routeKey(this.accountId)], signal)).sets[0]![0]?.value;
        const route = value === undefined ? undefined : accountRouteCodec.decode(value);
        if (route) {
            validateRoute(route, this.context, 0);
            if (route.account !== this.accountId) throw new ProtocolError('invalid_storage', 'Stored route belongs to another account.');
        }
        this.#route = route; this.#state = route && route.expiresAt > this.clock.nowSeconds() ? 'established' : 'unknown';
    }
    #accept(route: AccountRoute | undefined): boolean {
        const state = route ? 'established' : 'unknown';
        const changed = this.#state !== state || route !== undefined && accountRouteCodec.stringify(route) !== (this.#route && accountRouteCodec.stringify(this.#route));
        this.#state = state;
        if (route) this.#route = clone(route);
        return changed;
    }
    #changed(): void { this.#events.notify('accountChanged', { state: this.state, route: this.route }, error => this.notifyBackgroundError({ operation: 'observer', resource: 'accountChanged', error })); }
    async getRoute(accountId = this.accountId, signal?: AbortSignal): Promise<AccountRoute | undefined> {
        validateAccountId(accountId);
        const failures: BackgroundFailure[] = [];
        let changed = false;
        let route: AccountRoute | undefined;
        let operationError: unknown; let failed = false;
        try {
            route = await this.runOperation(scope => this.#gate.run(async () => {
                const resolved = await this.#resolve(accountId, failures, scope);
                if (accountId === this.accountId) changed = this.#accept(resolved);
                return resolved;
            }, scope), signal);
        } catch (error) { failed = true; operationError = error; }
        for (const failure of failures) this.notifyBackgroundError(failure);
        if (changed) this.#changed();
        if (failed) throw operationError;
        return route && clone(route);
    }
    async #resolve(accountId: string, failures: BackgroundFailure[], signal: AbortSignal): Promise<AccountRoute | undefined> {
        const previous = (await this.#store.read([routeKey(accountId)], signal)).sets[0]![0]?.value;
        const known = previous === undefined ? undefined : accountRouteCodec.decode(previous);
        let attempted = false;
        for await (const entry of this.#pool.registry.getRelays(signal)) {
            throwIfAborted(signal);
            if (entry.status !== 'active') continue;
            attempted = true;
            let route: AccountRoute;
            try {
                const client = await this.#pool.get(entry.relayId, undefined, signal);
                const response = await client.requestHttp('GET', 'account.route.resolve', { account: accountId }, { authenticated: false, signal });
                if (response === undefined) throw new ProtocolError('invalid_response', 'Route resolution returned no document.');
                route = accountRouteCodec.decode(response);
                if (route.account !== accountId) throw new ProtocolError('invalid_identity', 'Resolved route belongs to another account.');
                if (known && route.revision < known.revision) throw new ProtocolError('stale_route', 'Relay returned an older route.');
                await this.#verifyRelay(route, signal);
            } catch (error) {
                throwIfAborted(signal);
                if (error instanceof RelayError) {
                    if (error.code === 'not_found') continue;
                    if (!['temporarily_unavailable', 'bad_gateway', 'rate_limited'].includes(error.code)) throw error;
                } else if (!(error instanceof Error)) throw error;
                failures.push({ operation: 'connect', resource: entry.relayId, error }); continue;
            }
            await this.#save(route, signal); return route;
        }
        if (failures.length) throw new AggregateError(failures.map(value => value.error), 'Account route discovery failed through the available relays.');
        if (!attempted) throw new ProtocolError('no_active_relay', 'No active relay is available for route discovery.');
        return undefined;
    }
    async publishRoute(relayId: string, options: PublishRouteOptions): Promise<AccountRoute> {
        options = { ...options };
        validateRelayId(relayId); requireSafeInteger(options.validitySeconds, 1);
        if (options.validitySeconds > 3650 * 86400) throw new RangeError('Route validity cannot exceed 3650 days.');
        if (options.revision !== undefined) requireSafeInteger(options.revision, 0);
        const signer = this.#signer;
        if (!signer) throw new ProtocolError('signer_required', 'An account signer is required to publish a route.');
        let changed = false;
        const result = await this.runOperation(signal => this.#gate.run(async () => {
            const signed = await this.#prepare(relayId, options, signer, signal);
            const request = accountRouteCodec.decode(signed.document);
            const client = await this.#pool.get(relayId, { mode: 'account', signer }, signal);
            let response;
            try { response = await client.requestHttp('PUT', method, signed.document, { signal }); }
            catch (error) {
                if (error instanceof RelayError && error.isDefinitiveRejection) {
                    try { await finishSignedRequest(this.#store, method, signed); }
                    catch (storageError) { throw new AggregateError([error, storageError], 'Relay rejected publication but its durable result could not be saved.'); }
                }
                throw error;
            }
            if (response === undefined) throw new ProtocolError('invalid_response', 'Route publication returned no acknowledgement.');
            const route = accountRouteCodec.decode(response);
            const acknowledged = accountRouteCodec.encode(route); delete acknowledged.relay_signature;
            if (canonicalJson(acknowledged) !== accountRouteCodec.stringify(request)) throw new ProtocolError('invalid_response', 'Relay modified the submitted route.');
            await this.#verifyRelay(route, signal); await this.#save(route, signal);
            changed = this.#accept(route); return route;
        }, signal), options.signal);
        if (changed) this.#changed();
        return clone(result);
    }
    async #prepare(relayId: string, options: PublishRouteOptions, signer: AccountSigner, signal: AbortSignal): Promise<SignedRequest> {
        const queries = [routeKey(this.accountId), requestKey(method)];
        const snapshot = await this.#store.read(queries, signal);
        const previous = snapshot.sets[0]![0]?.value;
        const saved = snapshot.sets[1]![0]?.value;
        const known = previous === undefined ? undefined : accountRouteCodec.decode(previous);
        const signed = readSignedRequest(saved);
        const knownRevision = Math.max(known?.revision ?? -1, signed?.revision ?? -1);
        const pending = signed?.pending ? accountRouteCodec.decode(signed.document) : undefined;
        const now = this.clock.nowSeconds(); requireSafeInteger(now, 0);
        let revision = options.revision;
        if (options.recovery && revision === undefined && (!pending || signed!.relayId !== relayId || pending.expiresAt <= now))
            revision = Math.max(nextRevision(knownRevision), now * 1000);
        else if (revision === undefined && pending && signed!.relayId === relayId && pending.expiresAt <= now) revision = nextRevision(knownRevision);
        if (signed?.pending && (revision === undefined || revision === signed.revision)) {
            const route = pending!;
            validateRoute(route, this.context, now);
            if (route.account !== this.accountId || signed.relayId !== relayId || route.relayId !== relayId || route.revision !== signed.revision
                || route.expiresAt - route.updatedAt !== options.validitySeconds)
                throw new StateConflictError('A route publication has an unknown result. Retry the original publication or resolve it first.');
            return signed;
        }
        const publicKey = signer.publicKey.slice();
        if (signer.accountId !== this.accountId || !matchesAccount(this.accountId, publicKey)) throw new ProtocolError('invalid_identity', 'Account signer identity changed.');
        let request: AccountRoute = { account: this.accountId, accountPublicKey: publicKey, relayId, revision: nextRevision(knownRevision, revision),
            updatedAt: now, expiresAt: now + options.validitySeconds, accountSignature: new Uint8Array(64) };
        requireSafeInteger(request.expiresAt, 0);
        request = { ...request, accountSignature: (await signer.sign(routeAccountInput(request, this.context), signal)).slice() };
        validateRoute(request, this.context, this.clock.nowSeconds());
        const prepared: SignedRequest = { relayId, revision: request.revision, document: accountRouteCodec.encode(request), pending: true };
        await updateStore(this.#store, queries, current => {
            if (fingerprint(current.sets[0]![0]?.value) !== fingerprint(previous) || fingerprint(current.sets[1]![0]?.value) !== fingerprint(saved))
                throw new StateConflictError('The route changed while it was being signed. Resolve current state before retrying.');
            return { mutations: [putSignedRequest(method, prepared)], result: undefined };
        }, signal);
        return prepared;
    }
    async #verifyRelay(route: AccountRoute, signal: AbortSignal): Promise<void> {
        validateRoute(route, this.context, this.clock.nowSeconds());
        const client = await this.#pool.get(route.relayId, undefined, signal);
        const descriptor = await client.getDescriptor(signal);
        if (!route.relaySignature || !verifyRelay(route.relayId, descriptor.publicKey, routeRelayInput(route, this.context), route.relaySignature))
            throw new ProtocolError('invalid_signature', 'Account route lacks a valid signature from its home relay.');
    }
    async #save(route: AccountRoute, signal: AbortSignal): Promise<void> {
        await updateStore(this.#store, [routeKey(route.account), requestKey(method)], snapshot => {
            const current = snapshot.sets[0]![0]?.value;
            if (current) {
                const comparison = compareRoutes(route, accountRouteCodec.decode(current));
                if (comparison === 'older' || comparison === 'conflict') throw new ProtocolError('stale_route', 'Returned route is older or conflicts at the same revision.');
            }
            const mutations: StoreMutation[] = [{ kind: 'put', ...routeKey(route.account), value: accountRouteCodec.encode(route) }];
            const signed = readSignedRequest(snapshot.sets[1]![0]?.value);
            if (signed?.pending) {
                const request = accountRouteCodec.decode(signed.document);
                if (request.account === route.account && ['equivalent', 'newer'].includes(compareRoutes(route, request)))
                    mutations.push(putSignedRequest(method, { ...signed, pending: false }));
            }
            return { mutations, result: undefined };
        }, signal);
    }
    protected override async onDispose(): Promise<void> { this.#events.clear(); }
}
