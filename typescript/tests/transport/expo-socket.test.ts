import { afterEach, expect, test, vi } from 'vitest';
import { RpcConnection } from '@meshline/sdk';
import { nativeSocketFactory, type NativeSocketEvent, type NativeSocketModule } from '../../packages/expo/src/socket.js';

class Module implements NativeSocketModule {
    readonly listeners = new Set<(value: NativeSocketEvent) => void>();
    readonly calls: unknown[][] = [];
    id = 0;
    connectError: Error | undefined;
    connect(id: number, endpoint: string): void { this.id = id; this.calls.push(['connect', endpoint]); if (this.connectError) throw this.connectError; this.emit('open'); }
    send(id: number, text: string): void { this.calls.push(['send', id, text]); }
    close(id: number, code: number, reason: string): void { this.calls.push(['close', id, code, reason]); }
    cancel(id: number): void { this.calls.push(['cancel', id]); }
    bufferedAmount(id: number): number { return id === this.id ? 42 : 0; }
    addListener(_event: 'socket', listener: (event: NativeSocketEvent) => void) { this.listeners.add(listener); return { remove: () => { this.listeners.delete(listener); } }; }
    emit(type: NativeSocketEvent['type'], fields: Omit<NativeSocketEvent, 'id' | 'type'> = {}) { for (const listener of [...this.listeners]) listener({ id: this.id, type, ...fields }); }
}
afterEach(() => { vi.useRealTimers(); });

test('native connection is deferred until listeners exist and endpoint is normalized', async () => {
    const module = new Module(); const socket = nativeSocketFactory(module)('wss://例子.测试:443/relay'); const opened = vi.fn(); const messages: unknown[] = [];
    socket.on('open', opened); socket.on('message', value => messages.push(value)); expect(module.calls).toEqual([]);
    await Promise.resolve(); expect(opened).toHaveBeenCalledOnce(); expect(socket.readyState).toBe(1); expect(socket.bufferedAmount).toBe(42);
    expect(module.calls[0]).toEqual(['connect', 'wss://xn--fsqu00a.xn--0zwm56d/relay']);
    module.emit('text', { text: '中文😀' }); socket.send('reply 😀'); expect(messages).toEqual(['中文😀']);
    expect(() => socket.send('\ud800')).toThrow(); expect(module.calls).toHaveLength(2);
    module.emit('close', { code: 1000, reason: 'done' }); expect(socket.readyState).toBe(3); expect(socket.bufferedAmount).toBe(0); expect(module.listeners.size).toBe(0);
});

test('closing before native creation releases the event subscription without opening a socket', async () => {
    const module = new Module(); const socket = nativeSocketFactory(module)('wss://relay.example'); const closed = vi.fn(); socket.on('close', closed);
    expect(socket.close(1000, 'cancel')).toBe(1000); await Promise.resolve(); expect(module.calls).toEqual([]); expect(module.listeners.size).toBe(0); expect(closed).toHaveBeenCalledExactlyOnceWith({ code: 1000, reason: 'cancel' });
});

test.each([['binary', 1003], ['large', 1009]] as const)('RPC rejects %s content and sends the full native close code', async (kind, code) => {
    const module = new Module(); const rpc = new RpcConnection('wss://relay.example', { socketFactory: nativeSocketFactory(module) }); await rpc.connect();
    if (kind === 'binary') module.emit('binary'); else module.emit('text', { text: 'x'.repeat(1048577) });
    expect(await rpc.closed).toMatchObject({ requestedCloseCode: code, sentCloseCode: code }); expect(module.calls.some(call => call[0] === 'close' && call[2] === code)).toBe(true);
    module.emit('close', { code, reason: 'rejected' }); expect(module.listeners.size).toBe(0);
});

test('native creation failure rejects RPC connection and removes listeners', async () => {
    const module = new Module(); module.connectError = new Error('TLS handshake failed'); const rpc = new RpcConnection('wss://relay.example', { socketFactory: nativeSocketFactory(module) });
    await expect(rpc.connect()).rejects.toThrow('transport failed'); expect(module.listeners.size).toBe(0); expect(module.calls.some(call => call[0] === 'cancel')).toBe(true);
});

test('bounded close cancels an unresponsive native socket and detaches listeners', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const module = new Module(); const socket = nativeSocketFactory(module)('wss://relay.example'); await Promise.resolve(); const closed = vi.fn(); socket.on('close', closed);
    socket.close(1000, 'done'); expect(socket.readyState).toBe(2); await vi.advanceTimersByTimeAsync(3000);
    expect(module.calls.at(-1)).toEqual(['cancel', module.id]); expect(socket.readyState).toBe(3); expect(module.listeners.size).toBe(0); expect(closed.mock.calls[0]![0]).toMatchObject({ code: 1006 });
});

test('native events and listener removal stay isolated across concurrent sockets', async () => {
    const module = new Module(); const factory = nativeSocketFactory(module); const first = factory('wss://first.example'); await Promise.resolve(); const firstId = module.id;
    const second = factory('wss://second.example'); await Promise.resolve(); const secondId = module.id; const a = vi.fn(); const b = vi.fn(); const remove = first.on('message', a); second.on('message', b);
    module.emit('text', { text: 'second' }); expect(a).not.toHaveBeenCalled(); expect(b).toHaveBeenCalledExactlyOnceWith('second');
    module.id = firstId; module.emit('text', { text: 'first' }); remove(); module.emit('text', { text: 'later' }); expect(a).toHaveBeenCalledExactlyOnceWith('first');
    module.emit('close', { code: 1000 }); expect(module.listeners.size).toBe(1); module.id = secondId; module.emit('close', { code: 1000 }); expect(module.listeners.size).toBe(0);
});

test('invalid endpoints, reserved close codes and oversized UTF-8 reasons fail before native calls', async () => {
    const module = new Module(); const factory = nativeSocketFactory(module); expect(() => factory('ws://relay.example')).toThrow(); expect(module.listeners.size).toBe(0);
    const socket = factory('wss://relay.example'); await Promise.resolve(); expect(() => socket.close(1006, '')).toThrow(); expect(() => socket.close(1000, '😀'.repeat(31))).toThrow();
    expect(socket.readyState).toBe(1); expect(module.calls).toHaveLength(1); module.emit('close', { code: 1000 });
});
