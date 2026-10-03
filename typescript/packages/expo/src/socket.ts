import { encodeUtf8, webEndpoint, type RelaySocket, type RelaySocketEvents, type RelaySocketFactory } from '@meshline/sdk';

/** Internal Expo Modules contract, separated from module loading for adapter tests. */
export interface NativeSocketEvent {
    readonly id: number;
    readonly type: 'open' | 'text' | 'binary' | 'error' | 'close';
    readonly text?: string;
    readonly message?: string;
    readonly code?: number;
    readonly reason?: string;
}
export interface NativeSocketModule {
    connect(id: number, endpoint: string): void;
    send(id: number, text: string): void;
    close(id: number, code: number, reason: string): void;
    cancel(id: number): void;
    bufferedAmount(id: number): number;
    addListener(event: 'socket', listener: (event: NativeSocketEvent) => void): { remove(): void };
}

let nextId = 0;

/** The native implementation owns TLS, cookie/redirect policy and actual RFC close codes. */
export function nativeSocketFactory(module: NativeSocketModule): RelaySocketFactory {
    return endpoint => {
        const normalized = webEndpoint(endpoint, 'wss:').href;
        if (nextId === Number.MAX_SAFE_INTEGER) throw new Error('Native socket identifiers are exhausted.');
        const id = ++nextId;
        let state = 0; let connected = false; let timer: ReturnType<typeof setTimeout> | undefined;
        const listeners = new Map<keyof RelaySocketEvents, Set<(value: never) => void>>();
        function emit<K extends keyof RelaySocketEvents>(event: K, value: RelaySocketEvents[K]): void {
            for (const listener of [...listeners.get(event) ?? []]) listener(value as never);
        }
        function finish(code: number, reason: string): void {
            if (state === 3) return;
            state = 3; clearTimeout(timer); subscription.remove();
            try { emit('close', { code, reason }); } finally { listeners.clear(); }
        }
        function fail(error: unknown): void {
            if (state === 3) return;
            try { emit('error', error instanceof Error ? error : new Error(String(error))); }
            finally {
                try { if (connected) module.cancel(id); }
                finally { finish(1006, 'Native WebSocket failed.'); }
            }
        }
        const subscription = module.addListener('socket', event => {
            if (event.id !== id || state === 3) return;
            switch (event.type) {
                case 'open': if (state === 0) { state = 1; emit('open', undefined); } break;
                case 'text': if (state === 1) emit('message', event.text); break;
                case 'binary': if (state === 1) emit('message', new Uint8Array()); break;
                case 'error': fail(new Error(event.message ?? 'Native WebSocket failed.')); break;
                case 'close': finish(event.code ?? 1006, event.reason ?? ''); break;
            }
        });
        // RpcConnection installs all event handlers before native callbacks can fire.
        queueMicrotask(() => {
            if (state !== 0) return;
            connected = true;
            try { module.connect(id, normalized); } catch (error) { fail(error); }
        });
        const socket: RelaySocket = {
            get readyState() { return state; },
            get bufferedAmount() { return connected && state !== 3 ? module.bufferedAmount(id) : 0; },
            send(text) {
                if (state !== 1) throw new Error('Native WebSocket is not open.');
                // Reject malformed UTF-16 before a native bridge can replace it.
                encodeUtf8(text); module.send(id, text);
            },
            close(code, reason) {
                if (!Number.isInteger(code) || !([1000, 1001, 1002, 1003, 1007, 1008, 1009, 1010, 1011, 1012, 1013, 1014].includes(code) || code >= 3000 && code <= 4999)) throw new RangeError('Invalid WebSocket close code.');
                if (encodeUtf8(reason).length > 123) throw new RangeError('WebSocket close reason exceeds 123 UTF-8 bytes.');
                if (state >= 2) return code;
                state = 2;
                if (!connected) { queueMicrotask(() => finish(code, reason)); return code; }
                try { module.close(id, code, reason); } catch (error) { fail(error); throw error; }
                if (state !== 3) timer = setTimeout(() => {
                    try { module.cancel(id); } catch (error) { emit('error', error instanceof Error ? error : new Error(String(error))); }
                    finally { finish(1006, 'Native WebSocket close handshake timed out.'); }
                }, 3000);
                return code;
            },
            on(event, listener) {
                if (state === 3) return () => {};
                let set = listeners.get(event); if (!set) { set = new Set(); listeners.set(event, set); }
                set.add(listener as (value: never) => void);
                return () => { set.delete(listener as (value: never) => void); };
            },
        };
        return socket;
    };
}
