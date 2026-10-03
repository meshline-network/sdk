import { throwIfAborted, type RelayFetch, type RelayFetchInit, type RelayFetchResponse } from '@meshline/sdk';

interface NativeResponse extends Omit<RelayFetchResponse, 'body'> { readonly body: ReadableStream<Uint8Array> | null }

/** Read the abort/error-aware stream, not Expo's native arrayBuffer fast path:
 * that path can remain pending when native state becomes ERROR_RECEIVED. */
export function createExpoRelayFetch(fetch: (url: string, init: RelayFetchInit) => Promise<NativeResponse>): RelayFetch {
    return async (url, init) => {
        throwIfAborted(init.signal);
        const response = await fetch(url, { ...init, headers: { ...init.headers, 'Cache-Control': 'no-store, no-cache', Pragma: 'no-cache' }, credentials: 'omit', redirect: 'error' });
        const body = response.body; let consumed = false;
        return {
            status: response.status, headers: response.headers,
            ...(response.redirected === undefined ? {} : { redirected: response.redirected }), ...(response.type === undefined ? {} : { type: response.type }),
            body: body ? { async cancel() { consumed = true; await body.cancel(); } } : null,
            async arrayBuffer() {
                throwIfAborted(init.signal);
                if (consumed) throw new TypeError('Response body has already been consumed.'); consumed = true;
                if (!body) return new ArrayBuffer(0);
                const reader = body.getReader(); const chunks: Uint8Array[] = []; let length = 0;
                let cancellation: Promise<void> | undefined; let cancellationFailure: { error: unknown } | undefined;
                const abort = () => {
                    // Observe cancellation immediately, then propagate any failure
                    // in finally after the pending read settles and its lock releases.
                    cancellation = reader.cancel(init.signal.reason).then(() => undefined, error => { cancellationFailure = { error }; });
                };
                init.signal.addEventListener('abort', abort, { once: true });
                try {
                    for (;;) {
                        const chunk = await reader.read(); throwIfAborted(init.signal);
                        if (chunk.done) break; chunks.push(chunk.value); length += chunk.value.byteLength;
                    }
                    const bytes = new Uint8Array(length); let offset = 0;
                    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
                    return bytes.buffer;
                } finally {
                    init.signal.removeEventListener('abort', abort);
                    try { await cancellation; if (cancellationFailure) throw cancellationFailure.error; }
                    finally { reader.releaseLock(); }
                }
            },
        };
    };
}
