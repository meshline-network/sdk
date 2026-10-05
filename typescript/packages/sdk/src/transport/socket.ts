import { createAbortController, abortReason } from '../runtime/abort.js';
import { systemRandom, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { sessionCredentialsCodec, validateSession, type SessionMode } from '../models/authentication.js';
import { encodeBase64Url } from '../protocol/encoding.js';
import type { JsonObject, JsonValue } from '../protocol/json.js';
import { abortScope, awaitWithSignal, systemClock, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { webEndpoint } from './endpoint.js';
import { RelayBackoff } from './http.js';
import { RelayError } from './relay-error.js';
import { decodeRpcMessage, encodeRpcRequest, rpcFailureError, type RpcId, type RpcMessage, type RpcNotification } from './rpc.js';

export interface RelaySocketEvents {
    open: undefined;
    message: unknown;
    error: Error;
    close: { readonly code: number; readonly reason: string };
}

/** Platform socket adapters normalize events and report the actual close code they can send. */
export interface RelaySocket {
    readonly readyState: number;
    readonly bufferedAmount: number;
    send(text: string): void;
    close(code: number, reason: string): number;
    on<K extends keyof RelaySocketEvents>(event: K, listener: (value: RelaySocketEvents[K]) => void): () => void;
}
export type RelaySocketFactory = (endpoint: string) => RelaySocket;

/** Browsers only allow 1000 or application codes in close(); 3003/3009 report the corresponding local rejection. */
export const browserSocketFactory: RelaySocketFactory = endpoint => {
    const socket = new WebSocket(endpoint);
    socket.binaryType = 'arraybuffer';
    return {
        get readyState() { return socket.readyState; },
        get bufferedAmount() { return socket.bufferedAmount; },
        send(text) { socket.send(text); },
        close(code, reason) { const actual = code === 1000 ? code : 3000 + code - 1000; socket.close(actual, reason); return actual; },
        on(event, listener) {
            const handler = (value: Event): void => {
                const normalized = event === 'open' ? undefined : event === 'message' ? (value as MessageEvent<unknown>).data
                    : event === 'close' ? { code: (value as CloseEvent).code, reason: (value as CloseEvent).reason }
                        : new Error('The browser WebSocket failed; inspect the browser network diagnostics.');
                listener(normalized as RelaySocketEvents[typeof event]);
            };
            socket.addEventListener(event, handler);
            return () => socket.removeEventListener(event, handler);
        },
    };
};

export interface SocketFailure { readonly error: unknown; readonly requestedCloseCode: number; readonly sentCloseCode: number | undefined }
interface Pending { readonly method: string; resolve(message: Exclude<RpcMessage, { kind: 'notification' }>): void; reject(error: unknown): void }
export interface RpcConnectionOptions {
    readonly socketFactory?: RelaySocketFactory;
    readonly clock?: RuntimeClock;
    readonly random?: RandomSource;
    readonly backoff?: RelayBackoff;
    readonly requestTimeoutMilliseconds?: number;
}

/** One connection only. Pool/session owners handle reconnect, renewal and notification catch-up. */
export class RpcConnection {
    readonly #factory: RelaySocketFactory;
    readonly #clock: RuntimeClock;
    readonly #random: RandomSource;
    readonly #backoff: RelayBackoff;
    readonly #timeout: number;
    readonly #lifetime = createAbortController();
    readonly #pending = new Map<RpcId, Pending>();
    readonly #queue: RpcNotification[] = [];
    readonly #waiters: { resolve(value: RpcNotification): void; reject(error: unknown): void }[] = [];
    #socket: RelaySocket | undefined;
    #opening: Promise<void> | undefined;
    #rejectOpening: ((error: unknown) => void) | undefined;
    #mode: SessionMode | undefined;
    #sequence = 0;
    #requestPrefix: string | undefined;
    #failure: SocketFailure | undefined;
    #resolveClosed!: (failure: SocketFailure) => void;
    #unlisten: (() => void)[] = [];
    readonly closed = new Promise<SocketFailure>(resolve => { this.#resolveClosed = resolve; });

    constructor(readonly endpoint: string, options: RpcConnectionOptions = {}) {
        webEndpoint(endpoint, 'wss:');
        this.#factory = options.socketFactory ?? browserSocketFactory;
        this.#clock = options.clock ?? systemClock;
        this.#random = options.random ?? systemRandom;
        this.#backoff = options.backoff ?? new RelayBackoff(this.#clock);
        this.#timeout = options.requestTimeoutMilliseconds ?? 60000;
        if (!Number.isSafeInteger(this.#timeout) || this.#timeout < 1 || this.#timeout > 2_147_483_647) throw new RangeError('Invalid socket timeout.');
    }

    get failure(): SocketFailure | undefined { return this.#failure; }
    get establishedMode(): SessionMode | undefined { return this.#mode; }

    connect(signal?: AbortSignal): Promise<void> {
        throwIfAborted(signal);
        throwIfAborted(this.#lifetime.signal);
        if (this.#opening) return awaitWithSignal(this.#opening, signal);
        this.#requestPrefix = encodeBase64Url(this.#random.bytes(16));
        const scope = abortScope([this.#lifetime.signal], this.#timeout, "relay.websocket.connect");
        this.#opening = new Promise<void>((resolve, reject) => {
            this.#rejectOpening = reject;
            let socket: RelaySocket;
            try { socket = this.#factory(this.endpoint); this.#socket = socket; }
            catch (error) { this.#stop(error); reject(error); return; }
            const onAbort = (): void => { this.#stop(abortReason(scope.signal)); reject(abortReason(scope.signal)); };
            scope.signal.addEventListener('abort', onAbort, { once: true });
            this.#unlisten.push(() => scope.signal.removeEventListener('abort', onAbort));
            this.#unlisten.push(socket.on('open', () => {
                if (this.#failure) return;
                scope.dispose(); scope.signal.removeEventListener('abort', onAbort); this.#rejectOpening = undefined; resolve();
            }));
            this.#unlisten.push(socket.on('message', value => this.#receive(value)));
            this.#unlisten.push(socket.on('error', cause => {
                const error = new ProtocolError('socket_error', 'The relay WebSocket transport failed.', { cause });
                this.#stop(error); reject(error);
            }));
            this.#unlisten.push(socket.on('close', event => {
                const error = new ProtocolError('socket_closed', `Relay closed the WebSocket (${event.code}): ${event.reason}`);
                this.#stop(error); reject(error);
                this.#detach();
            }));
        });
        // Both branches handle cleanup; the original rejection remains visible to every waiter.
        this.#opening.then(() => scope.dispose(), () => scope.dispose());
        return awaitWithSignal(this.#opening, signal);
    }

    async request(method: string, parameters?: JsonObject, signal?: AbortSignal): Promise<JsonValue> {
        throwIfAborted(signal);
        throwIfAborted(this.#lifetime.signal);
        const scope = abortScope([signal, this.#lifetime.signal], this.#timeout, `relay.websocket.${method}`);
        let id: string | undefined;
        let sent = false;
        const authentication = method === 'auth.account.verify' || method === 'auth.device.verify';
        try {
            await this.connect(scope.signal);
            await this.#backoff.wait(scope.signal);
            if (this.#sequence === Number.MAX_SAFE_INTEGER) throw new ProtocolError('counter_overflow', 'Socket request sequence is exhausted.');
            id = `${this.#requestPrefix}:${++this.#sequence}`;
            const text = encodeRpcRequest(id, method, parameters);
            while (this.#socket!.bufferedAmount > 1_048_576) await this.#clock.delay(10, scope.signal);
            throwIfAborted(scope.signal);
            if (this.#socket!.readyState !== 1) throw new ProtocolError('socket_closed', 'WebSocket is not open.');
            const response = new Promise<Exclude<RpcMessage, { kind: 'notification' }>>((resolve, reject) => this.#pending.set(id!, { method, resolve, reject }));
            // Register a rejection handler before send, which may synchronously emit an error.
            const completion = awaitWithSignal(response, scope.signal);
            try { this.#socket!.send(text); sent = true; }
            catch (error) { this.#stop(error); }
            const message = await completion;
            if (message.kind === 'failure') {
                const error = rpcFailureError(message.error);
                if (error instanceof RelayError) {
                    this.#backoff.apply(error.failure);
                    if (error.code === 'unauthorized') this.#stop(error);
                }
                throw error;
            }
            return message.result;
        } catch (error) {
            if (authentication && sent && scope.signal.aborted) this.#stop(error);
            throw error;
        } finally { if (id !== undefined) this.#pending.delete(id); scope.dispose(); }
    }

    /** A single consumer normally drains this into the client notification dispatcher. */
    nextNotification(signal?: AbortSignal): Promise<RpcNotification> {
        throwIfAborted(signal);
        throwIfAborted(this.#lifetime.signal);
        const queued = this.#queue.shift();
        if (queued) return Promise.resolve(queued);
        return new Promise<RpcNotification>((resolve, reject) => {
            const waiter = { resolve: (value: RpcNotification) => { signal?.removeEventListener('abort', onAbort); resolve(value); },
                reject: (error: unknown) => { signal?.removeEventListener('abort', onAbort); reject(error); } };
            const onAbort = (): void => {
                const index = this.#waiters.indexOf(waiter);
                if (index >= 0) this.#waiters.splice(index, 1);
                waiter.reject(abortReason(signal!));
            };
            signal?.addEventListener('abort', onAbort, { once: true });
            this.#waiters.push(waiter);
        });
    }

    #receive(value: unknown): void {
        if (this.#failure) return;
        try {
            if (typeof value !== 'string') { this.#stop(new ProtocolError('binary_message', 'Relay WebSocket messages must use text.'), 1003); return; }
            const message = decodeRpcMessage(value);
            if (message.kind === 'notification') {
                if (this.#mode !== 'device') throw new ProtocolError('unauthorized_notification', 'Only an authenticated device connection can receive notifications.');
                const waiting = this.#waiters.shift();
                if (waiting) waiting.resolve(message.notification);
                else {
                    if (this.#queue.length >= 256) throw new ProtocolError('notification_overflow', 'Notification queue is full; reconnect and synchronize to recover missed updates.');
                    this.#queue.push(message.notification);
                }
                return;
            }
            if (message.id === null) throw new ProtocolError('uncorrelated_response', 'Relay response has no correlatable request ID.');
            const pending = this.#pending.get(message.id);
            // Late responses to canceled requests remain valid frames but have no waiting caller.
            if (!pending) return;
            if (message.kind === 'success' && (pending.method === 'auth.account.verify' || pending.method === 'auth.device.verify')) {
                const credentials = sessionCredentialsCodec.decode(message.result);
                validateSession(credentials);
                const mode = pending.method === 'auth.account.verify' ? 'account' : 'device';
                if (credentials.mode !== mode || this.#mode !== undefined && this.#mode !== mode) throw new ProtocolError('invalid_session', 'The relay changed the socket session mode.');
                // Set before resolving so a same-tick notification cannot race authentication.
                this.#mode = mode;
            }
            this.#pending.delete(message.id);
            pending.resolve(message);
        } catch (error) { this.#stop(error, error instanceof ProtocolError && error.code === 'message_too_large' ? 1009 : 1000); }
    }

    #stop(error: unknown, code = 1000): void {
        if (this.#failure) return;
        // Set before abort: abort handlers can reenter stop.
        this.#failure = { error, requestedCloseCode: code, sentCloseCode: undefined };
        this.#rejectOpening?.(error);
        this.#rejectOpening = undefined;
        this.#lifetime.abort(error);
        for (const pending of this.#pending.values()) pending.reject(error);
        this.#pending.clear();
        for (const waiter of this.#waiters.splice(0)) waiter.reject(error);
        this.#queue.length = 0;
        try {
            if (this.#socket && this.#socket.readyState < 2) {
                const sentCloseCode = this.#socket.close(code, code === 1003 ? 'Only text messages are supported.' : code === 1009 ? 'Message exceeds 1 MiB.' : 'Meshline connection ended.');
                this.#failure = { error, requestedCloseCode: code, sentCloseCode };
            }
        } catch (closeError) { this.#failure = { error: new AggregateError([error, closeError], 'Socket failure and close failure.'), requestedCloseCode: code, sentCloseCode: undefined }; }
        if (!this.#socket || this.#socket.readyState === 3) this.#detach();
        this.#resolveClosed(this.#failure);
    }

    #detach(): void {
        for (const unlisten of this.#unlisten) unlisten();
        this.#unlisten = [];
    }

    dispose(): void { this.#stop(new DOMException('The RPC connection was disposed.', 'AbortError')); }
    /** Retires this exact connection when authentication or subscription outcome is uncertain. */
    fail(error: unknown): void { this.#stop(error); }
}
