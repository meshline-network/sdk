import { request } from 'node:https';
import WebSocket from 'ws';
import { maxSocketMessageBytes, webEndpoint, type RelayFetch, type RelayFetchResponse, type RelaySocketEvents, type RelaySocketFactory } from '@meshline/sdk';

/** Optional explicit trust roots, e.g. a private network CA. TLS verification remains enabled. */
export interface NodeTransportOptions { readonly ca?: string | Uint8Array }

/** Cookie-free HTTPS with no redirects, caching, implicit retries or global TLS changes. */
export function createNodeRelayFetch(options: NodeTransportOptions = {}): RelayFetch {
    const ca = typeof options.ca === 'string' || options.ca === undefined ? options.ca : Buffer.from(options.ca);
    return (url, init) => new Promise<RelayFetchResponse>((resolve, reject) => {
        const endpoint = new URL(url);
        if (endpoint.protocol !== 'https:' || endpoint.username || endpoint.password) throw new TypeError('Expected a credential-free HTTPS request URL.');
        const outgoing = request(endpoint, { method: init.method, headers: init.headers, signal: init.signal, ...(ca === undefined ? {} : { ca }) }, response => {
            const headers = new Headers();
            for (let index = 0; index < response.rawHeaders.length; index += 2) headers.append(response.rawHeaders[index]!, response.rawHeaders[index + 1]!);
            resolve({
                status: response.statusCode!, headers,
                body: { async cancel() { response.destroy(); } },
                async arrayBuffer() {
                    const chunks: Uint8Array[] = [];
                    for await (const chunk of response) chunks.push(chunk as Uint8Array);
                    const buffer = Buffer.concat(chunks);
                    return buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength) as ArrayBuffer;
                },
            });
        });
        outgoing.on('error', reject);
        outgoing.end(init.body);
    });
}

/** Full RFC close codes and bounded frames; no cookies, redirects or compression negotiation. */
export function createNodeSocketFactory(options: NodeTransportOptions = {}): RelaySocketFactory {
    const ca = typeof options.ca === 'string' || options.ca === undefined ? options.ca : Buffer.from(options.ca);
    return endpoint => {
        webEndpoint(endpoint, 'wss:');
        // ws 8.22 supports closeTimeout; @types/ws 8.18 does not yet declare it.
        const socketOptions: WebSocket.ClientOptions & { closeTimeout: number } = { followRedirects: false, perMessageDeflate: false,
            maxPayload: maxSocketMessageBytes, handshakeTimeout: 60000, closeTimeout: 3000, ...(ca === undefined ? {} : { ca }) };
        const socket = new WebSocket(endpoint, socketOptions);
        return {
            get readyState() { return socket.readyState; },
            get bufferedAmount() { return socket.bufferedAmount; },
            send(text) { socket.send(text); },
            close(code, reason) { socket.close(code, reason); return code; },
            on(event, listener) {
                const handler = (...args: unknown[]): void => {
                    const value = event === 'open' ? undefined : event === 'error' ? args[0]
                        : event === 'close' ? { code: args[0], reason: String(args[1]) }
                            : args[1] ? args[0] : String(args[0]);
                    listener(value as RelaySocketEvents[typeof event]);
                };
                socket.on(event, handler);
                return () => { socket.off(event, handler); };
            },
        };
    };
}
