import { createAbortController } from '../runtime/abort.js';
import { ProtocolError } from '../errors.js';
import { validateAuthToken } from '../models/authentication.js';
import { canonicalJson, parseJson, requireObject, type JsonObject, type JsonValue } from '../protocol/json.js';
import { abortScope, systemClock, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { validateMethodName, webEndpoint } from './endpoint.js';
import { RelayError, relayFailureCodec, retryAfter, validateRetryAfterHeader, type RelayFailure } from './relay-error.js';

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
export interface RelayFetchInit {
    readonly method: HttpMethod;
    readonly headers: Readonly<Record<string, string>>;
    readonly body?: string;
    readonly signal: AbortSignal;
    readonly redirect: 'error';
    readonly credentials: 'omit';
    readonly cache: 'no-store';
    readonly referrerPolicy: 'no-referrer';
}
export interface RelayFetchResponse {
    readonly status: number;
    readonly redirected?: boolean;
    readonly type?: string;
    readonly headers: { get(name: string): string | null };
    readonly body?: { cancel(): Promise<void> } | null;
    arrayBuffer(): Promise<ArrayBuffer>;
}
/** An injected fetch must honor redirect:error and credentials:omit and must never replay a business request. */
export type RelayFetch = (url: string, init: RelayFetchInit) => Promise<RelayFetchResponse>;

/** Share between HTTP and WebSocket for one relay. A rejected request is never automatically retried. */
export class RelayBackoff {
    #until = 0;
    constructor(readonly clock: RuntimeClock = systemClock) {}
    apply(failure: RelayFailure): void {
        if (failure.code === 'rate_limited') this.#until = Math.max(this.#until, this.clock.monotonicMilliseconds() + Math.max(1, retryAfter(failure) ?? 1) * 1000);
    }
    async wait(signal?: AbortSignal): Promise<void> {
        for (;;) {
            throwIfAborted(signal);
            const remaining = this.#until - this.clock.monotonicMilliseconds();
            if (remaining <= 0) return;
            await this.clock.delay(Math.min(remaining, 60_000), signal);
        }
    }
}

/** Strict media-type parsing avoids accepting a second hidden non-UTF8 charset. */
export function validateJsonContentType(value: string | null): void {
    if (value === null) throw new ProtocolError('invalid_content_type', 'Relay responses require application/json with UTF-8 encoding.');
    const token = "[!#$%&'*+.^_`|~0-9a-z-]+";
    const match = /^\s*application\/json\s*/i.exec(value);
    if (!match) throw new ProtocolError('invalid_content_type', 'Relay responses require application/json with UTF-8 encoding.');
    let rest = value.slice(match[0].length);
    const parameter = new RegExp(`^;\\s*(${token})\\s*=\\s*(${token}|"(?:[^"\\\\\\r\\n]|\\\\[^\\r\\n])*")\\s*`, 'i');
    while (rest.length !== 0) {
        const part = parameter.exec(rest);
        if (!part) throw new ProtocolError('invalid_content_type', 'Malformed relay content type.');
        const parameterValue = part[2]!.startsWith('"') ? part[2]!.slice(1, -1).replace(/\\(.)/g, '$1') : part[2]!;
        if (part[1]!.toLowerCase() === 'charset' && parameterValue.toLowerCase() !== 'utf-8') throw new ProtocolError('invalid_content_type', 'Relay response charset must be UTF-8.');
        rest = rest.slice(part[0].length);
    }
}

function escapeQuery(value: string): string {
    return encodeURIComponent(value).replace(/[!'()*]/g, character => `%${character.charCodeAt(0).toString(16).toUpperCase()}`);
}

export interface HttpRelayTransportOptions { readonly fetch?: RelayFetch; readonly backoff?: RelayBackoff; readonly requestTimeoutMilliseconds?: number }

export class HttpRelayTransport {
    readonly #fetch: RelayFetch;
    readonly #lifetime = createAbortController();
    readonly #timeout: number;
    readonly backoff: RelayBackoff;
    constructor(options: HttpRelayTransportOptions = {}) {
        this.#fetch = options.fetch ?? ((url, init) => fetch(url, init));
        this.backoff = options.backoff ?? new RelayBackoff();
        this.#timeout = options.requestTimeoutMilliseconds ?? 60_000;
        if (!Number.isSafeInteger(this.#timeout) || this.#timeout < 1 || this.#timeout > 2_147_483_647) throw new RangeError('Invalid relay request timeout.');
    }

    async request(endpoint: string, method: HttpMethod, name: string, parameters?: JsonObject, token?: string, signal?: AbortSignal): Promise<JsonValue | undefined> {
        validateMethodName(name);
        if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].includes(method)) throw new ProtocolError('invalid_method', 'Unsupported HTTP method.');
        let address = webEndpoint(endpoint, 'https:').href.replace(/\/+$/, '') + '/' + name.replaceAll('.', '/');
        let body = parameters === undefined ? undefined : canonicalJson(requireObject(parameters));
        if (method === 'GET' && parameters !== undefined) {
            address += '?' + Object.entries(parameters).map(([key, value]) => {
                if (typeof value !== 'string' && typeof value !== 'number' && typeof value !== 'boolean')
                    throw new ProtocolError('invalid_query', 'GET parameters must be non-null scalars.');
                return `${escapeQuery(key)}=${escapeQuery(String(value))}`;
            }).join('&');
            body = undefined;
        }
        const headers: Record<string, string> = { Accept: 'application/json' };
        if (token !== undefined) { validateAuthToken(token); headers['X-Meshline-Session'] = token; }
        if (body !== undefined) headers['Content-Type'] = 'application/json; charset=utf-8';
        const lifetime = abortScope([signal, this.#lifetime.signal]);
        let deadline: ReturnType<typeof abortScope> | undefined;
        try {
            await this.backoff.wait(lifetime.signal);
            deadline = abortScope([lifetime.signal], this.#timeout);
            throwIfAborted(deadline.signal);
            const response = await this.#fetch(address, { method, headers, ...(body === undefined ? {} : { body }), signal: deadline.signal,
                redirect: 'error', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer' });
            if (response.redirected || response.type === 'opaqueredirect' || response.status >= 300 && response.status < 400) {
                await response.body?.cancel();
                throw new ProtocolError('redirect_rejected', 'Relay redirects are not permitted.');
            }
            if (response.status === 204) { await response.body?.cancel(); return undefined; }
            try { validateJsonContentType(response.headers.get('content-type')); }
            catch (error) { await response.body?.cancel(); throw error; }
            const value = parseJson(new Uint8Array(await response.arrayBuffer()));
            throwIfAborted(deadline.signal);
            if (response.status < 200 || response.status >= 300) {
                const failure = relayFailureCodec.decode(value);
                validateRetryAfterHeader(failure, response.status, response.headers.get('retry-after'));
                this.backoff.apply(failure);
                throw new RelayError(failure, response.status);
            }
            if (response.status !== 200) throw new ProtocolError('invalid_http_status', 'A method returning a result must return HTTP 200.');
            return value;
        } finally { deadline?.dispose(); lifetime.dispose(); }
    }

    /** Cancels active requests and makes later requests fail. */
    dispose(): void { this.#lifetime.abort(new DOMException('The HTTP relay transport was disposed.', 'AbortError')); }
}
