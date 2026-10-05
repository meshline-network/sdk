import { createAbortController } from '../runtime/abort.js';
import { systemRandom, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import type { RelayRegistry } from '../interactions.js';
import type { SessionMode } from '../models/authentication.js';
import type { RelayDescriptor, RelayInfo } from '../models/relay.js';
import type { JsonObject, JsonValue } from '../protocol/json.js';
import { abortScope, awaitWithSignal, systemClock, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import { RelayDiscovery } from './discovery.js';
import { HttpRelayTransport, RelayBackoff, type HttpMethod, type RelayFetch } from './http.js';
import { RelayError } from './relay-error.js';
import type { RpcNotification } from './rpc.js';
import { HttpRelaySessions, RelayAuthenticator, type RelaySession } from './session.js';
import { RpcConnection, type RelaySocketFactory } from './socket.js';
import { RelaySubscription, type RelaySubscriptionOptions, type SubscriptionMethod } from './subscription.js';

export type RelayConnectionState = 'disconnected' | 'connecting' | 'connected';
export type RelayAuthenticationState = 'none' | 'authenticating' | 'account' | 'device' | 'expired' | 'rejected';
export interface RelayClientState {
    readonly connection: RelayConnectionState;
    readonly authentication: RelayAuthenticationState;
    readonly sessionMode: SessionMode | undefined;
    readonly lastError: unknown;
}
export interface RelayClientEvents {
    readonly stateChanged: RelayClientState;
    readonly socketConnected: { readonly generation: number };
    readonly notificationReceived: RpcNotification;
    readonly errorOccurred: unknown;
    readonly faulted: unknown;
}
export interface RelayTransportOptions {
    readonly fetch?: RelayFetch;
    readonly socketFactory?: RelaySocketFactory;
    readonly clock?: RuntimeClock;
    readonly random?: RandomSource;
    readonly requestTimeoutMilliseconds?: number;
}
export interface HttpRequestOptions { readonly authenticated?: boolean; readonly signal?: AbortSignal }
interface SocketSlot { connection: RpcConnection; session: RelaySession; controller: AbortController; renewal: Promise<void>; generation: number }
function readiness() {
    let settled = false;
    let resolve!: (slot: SocketSlot) => void, reject!: (error: unknown) => void;
    const promise = new Promise<SocketSlot>((yes, no) => { resolve = yes; reject = no; });
    // A notification-only client may have no readiness waiter; retain errors for later callers.
    promise.then(() => undefined, () => undefined);
    return { promise, get settled() { return settled; }, resolve(slot: SocketSlot) { settled = true; resolve(slot); }, reject(error: unknown) { settled = true; reject(error); } };
}

function retryable(error: unknown): boolean {
    if (error instanceof RelayError) return ['unauthorized', 'temporarily_unavailable', 'bad_gateway', 'rate_limited'].includes(error.code);
    if (error instanceof ProtocolError) return ['socket_closed', 'socket_error', 'notification_overflow'].includes(error.code);
    if (error instanceof Error && error.name === 'TimeoutError') return true;
    // Fetch uses TypeError for connection failure; Node HTTPS preserves its system error code.
    return error instanceof TypeError || error instanceof Error && 'code' in error
        && ['ECONNRESET', 'ECONNREFUSED', 'ENOTFOUND', 'EAI_AGAIN', 'ETIMEDOUT', 'EPIPE'].includes(String(error.code));
}

/** Pool-owned transport for one relay and one immutable session identity. No constructor I/O. */
export class RelayClient {
    readonly #events = new EventHub<RelayClientEvents>();
    readonly #lifetime = createAbortController();
    readonly #clock: RuntimeClock;
    readonly #random: RandomSource;
    readonly #options: RelayTransportOptions;
    readonly #http: HttpRelayTransport;
    readonly #discovery: RelayDiscovery;
    readonly #sessions: HttpRelaySessions | undefined;
    readonly #authenticator: RelayAuthenticator | undefined;
    readonly #backoff: RelayBackoff;
    #httpSession: RelaySession | undefined;
    #socket: SocketSlot | undefined;
    #opening: Promise<SocketSlot> | undefined;
    #notifications: Promise<void> | undefined;
    #ready = readiness();
    #generation = 0;
    #connection: RelayClientState['connection'] = 'disconnected';
    #authenticating = false;
    #authenticationRejected = false;
    #lastError: unknown;
    #terminal: unknown;
    #disposal: Promise<void> | undefined;
    readonly #subscriptions = new Map<SubscriptionMethod, RelaySubscription>();

    constructor(readonly relayId: string, registry: RelayRegistry, options: RelayTransportOptions = {}, authenticator?: RelayAuthenticator) {
        this.#options = { ...options };
        this.#clock = options.clock ?? systemClock;
        this.#random = options.random ?? systemRandom;
        this.#backoff = new RelayBackoff(this.#clock);
        this.#http = new HttpRelayTransport({ ...options, backoff: this.#backoff });
        this.#discovery = new RelayDiscovery(relayId, registry, this.#http, this.#clock);
        if (authenticator && (authenticator.relayId !== relayId || authenticator.context.toString() !== registry.context.toString()))
            throw new ProtocolError('invalid_identity', 'Authenticator does not match this relay and network.');
        this.#authenticator = authenticator;
        if (authenticator) this.#sessions = new HttpRelaySessions(authenticator, this.#http);
    }

    get sessionMode(): SessionMode | undefined { return this.#authenticator?.mode; }
    get isDisposed(): boolean { return this.#lifetime.signal.aborted; }
    #hasValidSession(): boolean { return (this.#httpSession?.remainingSeconds ?? 0) > 0 || this.#socket !== undefined && !this.#socket.connection.failure && this.#socket.session.remainingSeconds > 0; }
    get state(): RelayClientState {
        const valid = this.#hasValidSession();
        return Object.freeze({ connection: this.#connection,
            authentication: this.isDisposed ? 'none' : this.#authenticationRejected ? 'rejected' : valid ? this.sessionMode! : this.#authenticating ? 'authenticating' : this.#httpSession || this.#socket ? 'expired' : 'none',
            sessionMode: this.sessionMode, lastError: this.#lastError });
    }
    on<K extends keyof RelayClientEvents>(event: K, listener: EventListener<RelayClientEvents[K]>): () => void { return this.#events.on(event, listener); }

    #check(signal?: AbortSignal): void { throwIfAborted(signal); throwIfAborted(this.#lifetime.signal); if (this.#terminal !== undefined) throw this.#terminal; }
    #notify<K extends Exclude<keyof RelayClientEvents, 'errorOccurred'>>(event: K, value: RelayClientEvents[K]): void {
        this.#events.notify(event, value, error => this.#publishError(this.#lastError === undefined ? error
            : new AggregateError([this.#lastError, error], 'Relay operation and observer failed.')));
    }
    #stateChanged(): void { this.#notify('stateChanged', this.state); }
    #publishError(error: unknown): void {
        this.#lastError = error;
        // Do not publish another state event for observer failures: a failing state
        // subscriber must not recursively trigger itself through error reporting.
        this.#events.notify('errorOccurred', error, observerError => {
            this.#lastError = new AggregateError([error, observerError], 'Relay operation and error observer failed.');
        });
    }
    #report(error: unknown): void {
        if (error instanceof RelayError && ['unauthorized', 'device_unknown', 'invalid_signature'].includes(error.code)) this.#authenticationRejected = true;
        this.#publishError(error); this.#stateChanged();
    }
    #acceptHttpSession(session: RelaySession): void {
        // A public response or a cached token is not evidence that a rejected
        // identity has authenticated again. Only a newly issued session clears it.
        if (session !== this.#httpSession) this.#authenticationRejected = false;
        this.#httpSession = session;
    }

    getDescriptor(signal?: AbortSignal): Promise<RelayDescriptor> { this.#check(signal); return this.#discovery.getDescriptor(signal); }
    getInfo(signal?: AbortSignal): Promise<RelayInfo> { this.#check(signal); return this.#discovery.getInfo(signal); }

    async authenticate(signal?: AbortSignal): Promise<void> {
        this.#check(signal);
        if (!this.#sessions) throw new ProtocolError('authentication_required', 'This relay client has no authentication identity.');
        if (!this.#hasValidSession()) this.#authenticationRejected = false;
        this.#authenticating = true;
        this.#connection = 'connecting';
        try {
            this.#stateChanged();
            const descriptor = await this.getDescriptor(signal);
            this.#acceptHttpSession(await this.#sessions.get(descriptor.endpoints.find(value => value.startsWith('https://'))!, signal));
            this.#check(signal);
            this.#connection = 'connected'; this.#lastError = undefined;
        } catch (error) { if (!signal?.aborted && !this.isDisposed) this.#report(error); throw error; }
        finally { this.#authenticating = false; this.#stateChanged(); }
    }

    async requestHttp(method: HttpMethod, name: string, parameters?: JsonObject, options: HttpRequestOptions = {}): Promise<JsonValue | undefined> {
        this.#check(options.signal);
        const scope = abortScope([options.signal, this.#lifetime.signal]);
        let session: RelaySession | undefined;
        try {
            const descriptor = await this.getDescriptor(scope.signal);
            const endpoint = descriptor.endpoints.find(value => value.startsWith('https://'))!;
            if (options.authenticated !== false) {
                if (!this.#sessions) throw new ProtocolError('authentication_required', 'Establish a relay session before an authenticated request.');
                session = await this.#sessions.get(endpoint, scope.signal); this.#acceptHttpSession(session);
            }
            const result = await this.#http.request(endpoint, method, name, parameters, session?.getToken(endpoint), scope.signal);
            this.#connection = 'connected'; this.#lastError = undefined; this.#stateChanged();
            return result;
        } catch (error) {
            if (error instanceof RelayError && error.code === 'unauthorized' && session) {
                this.#sessions!.invalidate(session); if (this.#httpSession === session) this.#httpSession = undefined;
            }
            if (!scope.signal.aborted) this.#report(error);
            throw error;
        } finally { scope.dispose(); }
    }

    async #getSocket(signal?: AbortSignal): Promise<SocketSlot> {
        this.#check(signal);
        if (!this.#authenticator) throw new ProtocolError('authentication_required', 'WebSocket connections require an authentication identity.');
        if (!this.#opening) {
            const opening = this.#openSocket(); this.#opening = opening;
            opening.then(() => { if (this.#opening === opening) this.#opening = undefined; }, () => { if (this.#opening === opening) this.#opening = undefined; });
        }
        return awaitWithSignal(this.#opening, signal);
    }

    async #openSocket(): Promise<SocketSlot> {
        const descriptor = await this.getDescriptor(this.#lifetime.signal);
        const endpoint = descriptor.endpoints.find(value => value.startsWith('wss://'));
        if (!endpoint) throw new ProtocolError('socket_unavailable', 'The relay does not advertise WebSocket support.');
        const existing = this.#socket;
        if (existing && existing.connection.endpoint === endpoint && !existing.connection.failure && existing.session.remainingSeconds > 0) return existing;
        if (existing) { existing.controller.abort(); existing.connection.dispose(); await existing.renewal; this.#socket = undefined; }
        const connection = new RpcConnection(endpoint, { ...this.#options, clock: this.#clock, random: this.#random, backoff: this.#backoff });
        const timeout = abortScope([this.#lifetime.signal], this.#options.requestTimeoutMilliseconds ?? 60000, "relay.websocket.connect");
        this.#connection = 'connecting'; this.#stateChanged();
        try {
            const session = await this.#authenticator!.authenticate(endpoint, (name, params, signal) => connection.request(name, params, signal), timeout.signal);
            this.#check(timeout.signal);
            const slot: SocketSlot = { connection, session, controller: createAbortController(), renewal: Promise.resolve(), generation: ++this.#generation };
            this.#socket = slot;
            this.#authenticationRejected = false;
            slot.renewal = this.#renew(slot);
            this.#connection = 'connected'; this.#lastError = undefined; this.#stateChanged();
            return slot;
        } catch (error) {
            connection.dispose();
            if (this.#socket?.connection === connection) {
                const failed = this.#socket; this.#socket = undefined;
                failed.controller.abort(error); await failed.renewal;
            }
            throw error;
        }
        finally { timeout.dispose(); }
    }

    async #renew(slot: SocketSlot): Promise<void> {
        const scope = abortScope([this.#lifetime.signal, slot.controller.signal]);
        try {
            while (!scope.signal.aborted) {
                const previous = slot.session;
                const remaining = previous.remainingSeconds;
                if (remaining <= 0) throw new ProtocolError('expired_session', 'The WebSocket session expired.');
                const due = this.#clock.monotonicMilliseconds() + Math.max(remaining * 0.8, remaining - 30) * 1000;
                while (due > this.#clock.monotonicMilliseconds()) await this.#clock.delay(Math.min(60000, due - this.#clock.monotonicMilliseconds()), scope.signal);
                const deadline = abortScope([scope.signal], Math.max(1, Math.min(60000, Math.floor(previous.remainingSeconds * 1000))), "relay.websocket.renew");
                try {
                    const renewed = await this.#authenticator!.authenticate(slot.connection.endpoint, (name, params, signal) => slot.connection.request(name, params, signal), deadline.signal);
                    if (previous.remainingSeconds <= 0) throw new ProtocolError('expired_session', 'Previous socket session expired before renewal completed.');
                    slot.session = renewed; this.#authenticationRejected = false; this.#stateChanged();
                } catch (error) {
                    if (error instanceof RelayError && error.code !== 'unauthorized' && previous.remainingSeconds > 0) {
                        this.#report(error);
                        // A rejected renewal other than unauthorized leaves the
                        // already accepted socket session usable until expiry.
                        this.#authenticationRejected = false; this.#stateChanged();
                        await this.#clock.delay(Math.min(5000, previous.remainingSeconds * 1000), scope.signal);
                    } else throw error;
                } finally { deadline.dispose(); }
            }
        } catch (error) {
            if (!scope.signal.aborted) { this.#report(error); slot.connection.fail(error); }
        } finally { scope.dispose(); }
    }

    /** Device notification reconnection restores transport only; callers synchronize after socketConnected. */
    startNotifications(): void {
        this.#check();
        if (this.sessionMode !== 'device') throw new ProtocolError('device_session_required', 'Notifications require a device session.');
        this.#notifications ??= this.#dispatchNotifications();
    }

    async #dispatchNotifications(): Promise<void> {
        let failures = 0;
        try {
            while (!this.isDisposed) {
                try {
                    const slot = await this.#getSocket(this.#lifetime.signal);
                    this.#ready.resolve(slot);
                    this.#notify('socketConnected', { generation: slot.generation });
                    failures = 0;
                    for (;;) {
                        const notification = await slot.connection.nextNotification(this.#lifetime.signal);
                        if (slot.session.remainingSeconds <= 0) throw new ProtocolError('expired_session', 'A notification arrived after socket session expiry.');
                        this.#notify('notificationReceived', notification);
                    }
                } catch (error) {
                    if (this.isDisposed) break;
                    this.#connection = 'disconnected';
                    if (this.#ready.settled) this.#ready = readiness();
                    this.#report(error);
                    if (!retryable(error)) {
                        this.#terminal = error; this.#ready.reject(error);
                        this.#socket?.controller.abort(error); this.#socket?.connection.fail(error);
                        this.#notify('faulted', error);
                        return;
                    }
                    const jitter = this.#random.bytes(2); const fraction = ((jitter[0]! << 8) | jitter[1]!) / 65536;
                    await this.#clock.delay((Math.min(30, 2 ** Math.min(failures++, 5)) + fraction) * 1000, this.#lifetime.signal);
                }
            }
        } catch (error) { if (!this.isDisposed) { this.#terminal = error; this.#ready.reject(error); this.#report(error); } }
    }

    async requestWebSocket(method: string, parameters?: JsonObject, signal?: AbortSignal): Promise<JsonValue> {
        this.#check(signal);
        const deadline = abortScope([signal, this.#lifetime.signal], this.#options.requestTimeoutMilliseconds ?? 60000, `relay.websocket.${method}`);
        try {
            let slot: SocketSlot;
            if (this.sessionMode === 'device') { this.startNotifications(); slot = await awaitWithSignal(this.#ready.promise, deadline.signal); }
            else slot = await this.#getSocket(deadline.signal);
            const result = await slot.connection.request(method, parameters, deadline.signal);
            this.#connection = 'connected'; this.#lastError = undefined; this.#stateChanged();
            return result;
        } catch (error) { if (!deadline.signal.aborted) this.#report(error); throw error; }
        finally { deadline.dispose(); }
    }

    /** Owns the complete replacement stream for this method; dispose it after stopping its update producers. */
    createSubscription(method: SubscriptionMethod, empty: JsonObject, options: RelaySubscriptionOptions = {}): RelaySubscription {
        this.#check(options.signal);
        if (this.sessionMode !== 'device') throw new ProtocolError('device_session_required', 'Subscriptions require a device session.');
        if (method !== 'channel.subscribe' && method !== 'group.subscribe') throw new TypeError('Unknown subscription method.');
        if (this.#subscriptions.has(method)) throw new ProtocolError('subscription_in_use', 'This client already has an owner for the subscription method.');
        const subscription = new RelaySubscription(method, empty, { ...options }, {
            clock: this.#clock, lifetime: this.#lifetime.signal, onConnected: listener => this.on('socketConnected', listener),
            ready: async signal => {
                this.startNotifications(); const slot = await awaitWithSignal(this.#ready.promise, signal);
                return { generation: slot.generation, get failure() { return slot.connection.failure; },
                    request: (name, params, token) => slot.connection.request(name, params, token), retire: error => slot.connection.fail(error) };
            },
            closed: () => { if (this.#subscriptions.get(method) === subscription) this.#subscriptions.delete(method); },
        });
        this.#subscriptions.set(method, subscription); return subscription;
    }

    dispose(): Promise<void> {
        return this.#disposal ??= (async () => {
            const reason = new DOMException('Relay client was disposed.', 'AbortError');
            this.#lifetime.abort(reason); this.#ready.reject(reason);
            this.#sessions?.dispose(); this.#discovery.dispose(); this.#http.dispose();
            this.#socket?.controller.abort(reason); this.#socket?.connection.dispose();
            await Promise.allSettled([this.#opening, this.#notifications, this.#socket?.renewal, ...[...this.#subscriptions.values()].map(value => value.dispose())]);
            this.#connection = 'disconnected';
            this.#stateChanged(); this.#events.clear();
        })();
    }
}
