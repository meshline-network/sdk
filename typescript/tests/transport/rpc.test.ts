import { expect, test } from 'vitest';
import { RpcConnection, decodeRpcMessage, encodeRpcRequest, type RelaySocket, type RelaySocketEvents, type RpcNotification } from '@meshline/sdk';

test('responses correlate opaque IDs and preserve business extensions while ignoring wrapper extensions', () => {
    expect(decodeRpcMessage('{"jsonrpc":"2.0","id":1,"result":{"future":null},"method":"extension","params":null}'))
        .toEqual({ kind: 'success', id: 1, result: { future: null } });
    expect(decodeRpcMessage('{"jsonrpc":"2.0","id":"1","error":{"code":-32011,"message":"conflict","data":{"revision":7}},"extra":true}'))
        .toEqual({ kind: 'failure', id: '1', error: { code: -32011, message: 'conflict', data: { revision: 7 } } });
    expect(decodeRpcMessage('{"jsonrpc":"2.0","method":"future.changed","params":{"next":1},"extra":true}'))
        .toEqual({ kind: 'notification', notification: { method: 'future.changed', params: { next: 1 } } });
});

test.each([
    '[]', 'null', '{}', '{"jsonrpc":"1.0","id":"x","result":null}',
    '{"jsonrpc":"2.0","id":null,"result":null}', '{"jsonrpc":"2.0","id":"x"}',
    '{"jsonrpc":"2.0","id":"x","result":null,"error":{}}', '{"jsonrpc":"2.0","id":{},"result":null}',
    '{"jsonrpc":"2.0","id":1.0,"result":null}', '{"jsonrpc":"2.0","id":9007199254740992,"result":null}',
    '{"jsonrpc":"2.0","id":"x","result":null,"future":1,"future":2}',
    '{"jsonrpc":"2.0","id":"x","error":{"code":-32011,"message":"bad","data":null}}',
    '{"jsonrpc":"2.0","method":"x","params":null}',
])('rejects invalid RPC %s', text => { expect(() => decodeRpcMessage(text)).toThrow(); });

test('RPC size and depth limits include ignored extensions', () => {
    expect(() => decodeRpcMessage(JSON.stringify({ jsonrpc: '2.0', id: 'x', result: null, future: 'x'.repeat(1048576) }))).toThrow('1 MiB');
    expect(() => encodeRpcRequest('x', 'message.send', { content: 'x'.repeat(1048576) })).toThrow('1 MiB');
    expect(() => encodeRpcRequest('😀'.repeat(33), 'relay.info')).toThrow('128');
    let value: unknown = null;
    for (let i = 0; i < 17; i++) value = { child: value };
    expect(() => decodeRpcMessage(JSON.stringify({ jsonrpc: '2.0', id: 'x', result: null, future: value }))).toThrow();
});

class ScriptedSocket implements RelaySocket {
    readyState = 0;
    bufferedAmount = 0;
    readonly sent: { id: string; method: string }[] = [];
    readonly closeCodes: number[] = [];
    readonly listeners = new Map<string, Set<(value: never) => void>>();
    send(text: string): void { this.sent.push(JSON.parse(text) as { id: string; method: string }); }
    close(code: number): number { this.closeCodes.push(code); this.readyState = 3; this.emit('close', { code, reason: '' }); return code; }
    on<K extends keyof RelaySocketEvents>(event: K, listener: (value: RelaySocketEvents[K]) => void): () => void {
        const list = this.listeners.get(event) ?? new Set(); this.listeners.set(event, list); list.add(listener as (value: never) => void);
        return () => { list.delete(listener as (value: never) => void); };
    }
    emit<K extends keyof RelaySocketEvents>(event: K, value: RelaySocketEvents[K]): void { for (const listener of this.listeners.get(event) ?? []) listener(value as never); }
    respond(index: number, result: unknown): void { this.emit('message', JSON.stringify({ jsonrpc: '2.0', id: this.sent[index]!.id, result })); }
}
async function connected(options = {}): Promise<{ socket: ScriptedSocket; connection: RpcConnection }> {
    const socket = new ScriptedSocket();
    const connection = new RpcConnection('wss://relay.example/meshline/v1', { ...options, socketFactory: () => socket });
    const connecting = connection.connect(); socket.readyState = 1; socket.emit('open', undefined); await connecting;
    return { socket, connection };
}
async function flush(): Promise<void> { for (let i = 0; i < 6; i++) await Promise.resolve(); }

test('concurrent requests correlate out-of-order responses without replay', async () => {
    const { socket, connection } = await connected();
    const a = connection.request('relay.info'), b = connection.request('account.route.resolve', { account: 'x' });
    await flush();
    expect(socket.sent).toHaveLength(2);
    expect(socket.sent[0]!.id).not.toBe(socket.sent[1]!.id);
    socket.respond(1, { value: 'b' }); socket.respond(0, { value: 'a' });
    expect(await a).toEqual({ value: 'a' }); expect(await b).toEqual({ value: 'b' });
    connection.dispose();
});

test('canceled request releases its correlation slot and a late response cannot complete another request', async () => {
    const { socket, connection } = await connected();
    const controller = new AbortController();
    const request = connection.request('message.send', {}, controller.signal);
    const assertion = expect(request).rejects.toThrow('canceled');
    await flush(); controller.abort(new Error('canceled')); await assertion;
    const next = connection.request('relay.info'); await flush();
    socket.respond(0, { stale: true }); socket.respond(1, { current: true });
    expect(await next).toEqual({ current: true });
    expect(socket.sent).toHaveLength(2); expect(connection.failure).toBeUndefined(); connection.dispose();
});

test('device authentication response establishes notification permission before the next synchronous frame', async () => {
    const { socket, connection } = await connected();
    const auth = connection.request('auth.device.verify', {}); await flush();
    socket.respond(0, { token: 'test', mode: 'device', expires_at: 2000000000 });
    socket.emit('message', '{"jsonrpc":"2.0","method":"message.timeline.changed","params":{"after":1}}');
    await auth;
    expect(await connection.nextNotification()).toEqual({ method: 'message.timeline.changed', params: { after: 1 } });
    connection.dispose();
});

test('unauthenticated notifications fail the connection and all pending work', async () => {
    const { socket, connection } = await connected();
    const request = connection.request('auth.challenge'); const failure = expect(request).rejects.toThrow('device'); await flush();
    socket.emit('message', '{"jsonrpc":"2.0","method":"message.timeline.changed"}');
    await failure; expect((await connection.closed).error).toMatchObject({ code: 'unauthorized_notification' });
});

test('a stalled consumer causes a visible queue overflow rather than losing notifications silently', async () => {
    const { socket, connection } = await connected();
    const auth = connection.request('auth.device.verify'); await flush(); socket.respond(0, { token: 't', mode: 'device', expires_at: 2000000000 }); await auth;
    for (let i = 0; i < 257; i++) socket.emit('message', '{"jsonrpc":"2.0","method":"future.changed"}');
    expect((await connection.closed).error).toMatchObject({ code: 'notification_overflow' });
});

test.each([[new Uint8Array([1]), 1003], [JSON.stringify({ jsonrpc: '2.0', id: 'x', result: 'x'.repeat(1048576) }), 1009]] as const)('rejects binary and oversized messages with protocol close codes', async (frame, code) => {
    const { socket, connection } = await connected(); socket.emit('message', frame);
    expect((await connection.closed).sentCloseCode).toBe(code); expect(socket.closeCodes).toEqual([code]);
});

test('disposal during connection establishment rejects the original connect promise', async () => {
    const socket = new ScriptedSocket();
    const connection = new RpcConnection('wss://relay.example', { socketFactory: () => socket });
    const opening = connection.connect(); const assertion = expect(opening).rejects.toThrow('disposed');
    connection.dispose(); await assertion;
    expect([...socket.listeners.values()].every(list => list.size === 0)).toBe(true);
});

test('canceling an authentication proof retires its uncertain connection', async () => {
    const { socket, connection } = await connected(); const controller = new AbortController();
    const auth = connection.request('auth.account.verify', {}, controller.signal); const assertion = expect(auth).rejects.toThrow('canceled');
    await flush(); controller.abort(new Error('canceled')); await assertion;
    expect(connection.failure).toBeDefined(); expect(socket.closeCodes).toEqual([1000]);
});

test('unauthorized response ends a connection without replaying the rejected operation', async () => {
    const { socket, connection } = await connected(); const result = connection.request('message.send', {}); await flush();
    socket.emit('message', JSON.stringify({ jsonrpc: '2.0', id: socket.sent[0]!.id, error: { code: -32001, message: 'expired' } }));
    await expect(result).rejects.toMatchObject({ code: 'unauthorized', isDefinitiveRejection: true });
    expect(socket.sent).toHaveLength(1); expect(connection.failure).toBeDefined();
});
