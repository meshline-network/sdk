import { afterEach, expect, test, vi } from 'vitest';
import {
    RelayClientPool, deviceCertificateCodec, decodeBase64Url, relayDescriptorCodec, signDevice, rpcErrorCodes,
    deviceAuthenticationCodec, deviceAuthenticationInput, verifyDevice, type AuthenticationIdentity, type JsonObject,
    type JsonValue, type RelayFetch, type RelaySocket, type RelaySocketEvents, type RelaySubscription,
} from '../../packages/sdk/src/index.js';
import { createNodeRelayFetch, createNodeSocketFactory } from '../../packages/transport-node/src/index.js';
import { context, signedDescriptor, TestRegistry } from '../support/relay-fixture.js';
import { AdvancingClock } from '../support/clock.js';
import { vector } from '../support/vectors.js';
import { startTlsPeer, tlsMaterial } from '../support/tls-peer.js';

const fixture = vector<{ device_certificate: { unsigned_object: JsonObject; account_signature: string; device_signature: string; device_private_key: string } }>('identity-auth').device_certificate;
const certificate = deviceCertificateCodec.decode({ ...fixture.unsigned_object, account_signature: fixture.account_signature, device_signature: fixture.device_signature });
const identity: AuthenticationIdentity = { mode: 'device', signer: { certificate, sign: async input => signDevice(input, decodeBase64Url(fixture.device_private_key)) } };
const resources: { dispose(): Promise<void> }[] = [];
afterEach(async () => { for (const value of resources.splice(0).reverse()) await value.dispose(); });
class ControlledClock extends AdvancingClock {
    readonly waits = new Set<{ milliseconds: number; finish(): void }>();
    override delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
        signal?.throwIfAborted();
        return new Promise((resolve, reject) => {
            const cleanup = (): void => { this.waits.delete(wait); signal?.removeEventListener('abort', abort); };
            const abort = (): void => { cleanup(); reject(signal!.reason); };
            const wait = { milliseconds, finish: () => { cleanup(); this.elapsed += milliseconds; resolve(); } };
            this.waits.add(wait); signal?.addEventListener('abort', abort, { once: true });
        });
    }
    advanceShort(): void { for (const wait of [...this.waits]) if (wait.milliseconds <= 5000) wait.finish(); }
}
interface Frame { socket: Socket; method: string; params: JsonObject; ack(value?: JsonValue): void; reject(code: string): void }
class Socket implements RelaySocket {
    readyState = 0; bufferedAmount = 0; readonly listeners = new Map<string, Set<(value: never) => void>>();
    constructor(readonly receive: (frame: Frame) => void) { queueMicrotask(() => { this.readyState = 1; this.emit('open', undefined); }); }
    on<K extends keyof RelaySocketEvents>(event: K, listener: (value: RelaySocketEvents[K]) => void): () => void {
        const set = this.listeners.get(event) ?? new Set(); this.listeners.set(event, set); set.add(listener as (value: never) => void); return () => { set.delete(listener as (value: never) => void); };
    }
    emit<K extends keyof RelaySocketEvents>(event: K, value: RelaySocketEvents[K]): void { for (const listener of this.listeners.get(event) ?? []) listener(value as never); }
    close(code: number): number { this.readyState = 3; this.emit('close', { code, reason: '' }); return code; }
    send(text: string): void {
        const request = JSON.parse(text) as { method: string; id: string; params: JsonObject };
        this.receive({ socket: this, method: request.method, params: request.params,
            ack: (result = null) => this.emit('message', JSON.stringify({ jsonrpc: '2.0', id: request.id, result })),
            reject: code => this.emit('message', JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: rpcErrorCodes[code], message: code } })),
        });
    }
}
async function setup() {
    const clock = new ControlledClock(); const descriptor = signedDescriptor(); const sockets: Socket[] = []; const requests: Frame[] = []; const challenges: Frame[] = [];
    let holdAuthentication = false; let handler = (frame: Frame): void => frame.ack();
    const challenge = (): JsonObject => ({ nonce: 'challenge', created_at: clock.wall, expires_at: clock.wall + 300 });
    const fetch: RelayFetch = async url => new Response(JSON.stringify(url.endsWith('relay/descriptor') ? relayDescriptorCodec.encode(descriptor)
        : url.endsWith('auth/challenge') ? challenge() : { token: 'device-session', mode: 'device', expires_at: clock.wall + 3600 }), { headers: { 'content-type': 'application/json' } });
    const pool = new RelayClientPool({ context, accountId: certificate.account, fetch, clock, socketFactory: () => {
        const socket = new Socket(frame => {
            if (frame.method === 'auth.challenge') { if (holdAuthentication) challenges.push(frame); else frame.ack(challenge()); }
            else if (frame.method === 'auth.device.verify') {
                const proof = deviceAuthenticationCodec.decode(frame.params);
                expect(verifyDevice(deviceAuthenticationInput(proof, descriptor.relayId, 'wss://relay.example/v1', context), proof.deviceSignature, proof.signerCertificate.signingPublicKey)).toBe(true);
                frame.ack({ token: 'socket-device', mode: 'device', expires_at: clock.wall + 3600 });
            } else { requests.push(frame); handler(frame); }
        }); sockets.push(socket); return socket;
    } }, new TestRegistry()); resources.push(pool);
    const client = await pool.get(descriptor.relayId, identity);
    return { client, pool, clock, sockets, requests, challenges,
        set handler(value: (frame: Frame) => void) { handler = value; }, set holdAuthentication(value: boolean) { holdAuthentication = value; },
        releaseAuthentication() { holdAuthentication = false; for (const frame of challenges.splice(0)) frame.ack(challenge()); },
        subscription(options: Parameters<typeof client.createSubscription>[2] = {}) { return client.createSubscription('channel.subscribe', { channel_ids: [] }, options); },
    };
}
const until = async (condition: () => boolean) => { await vi.waitFor(() => expect(condition()).toBe(true), { timeout: 3000, interval: 5 }); };

test('replacement waits for readiness, snapshots/coalesces desired sets, skips duplicate ACKs and clears on disposal', async () => {
    const f = await setup(); f.holdAuthentication = true; let acknowledged = 0;
    const subscription = f.subscription({ onSubscribed: () => { acknowledged++; } });
    subscription.update({ channel_ids: ['one'] }); await until(() => f.challenges.length === 1);
    const desired = { channel_ids: ['two', 'three'] }; subscription.update(desired); desired.channel_ids.push('external-mutation');
    expect(f.requests).toEqual([]); f.releaseAuthentication(); await until(() => acknowledged === 1);
    expect(f.requests[0]!.params).toEqual({ channel_ids: ['two', 'three'] });
    subscription.update({ channel_ids: ['two', 'three'] }); await new Promise(resolve => setTimeout(resolve, 20)); expect(f.requests).toHaveLength(1);
    await subscription.dispose(); expect(f.requests.map(frame => frame.params)).toEqual([{ channel_ids: ['two', 'three'] }, { channel_ids: [] }]);
    expect(f.sockets[0]!.readyState).toBe(1); expect(acknowledged).toBe(1); expect(subscription.lastError).toBeUndefined();
});

test('in-flight replacement finishes before the latest follow/unfollow update and never overlaps', async () => {
    const f = await setup(); f.handler = () => {}; const subscription = f.subscription();
    subscription.update({ channel_ids: ['one'] }); await until(() => f.requests.length === 1);
    subscription.update({ channel_ids: [] }); subscription.update({ channel_ids: ['latest'] }); expect(f.requests).toHaveLength(1);
    f.handler = frame => frame.ack(); f.requests[0]!.ack(); await until(() => f.requests.length === 2);
    expect(f.requests[1]!.params).toEqual({ channel_ids: ['latest'] }); await subscription.dispose(); expect(f.requests[2]!.params).toEqual({ channel_ids: [] });
});

test('a malformed replacement ACK retires its connection before the newest set is sent after reconnection', async () => {
    const f = await setup(); let acknowledgements = 0; const errors: unknown[] = [];
    f.handler = frame => { if (f.requests.length === 1) frame.ack({ unexpected: true }); else frame.ack(); };
    const subscription = f.subscription({ onSubscribed: () => { acknowledgements++; }, onError: error => { errors.push(error); } });
    subscription.update({ channel_ids: ['old'] }); await until(() => errors.length === 1 && f.sockets[0]!.readyState === 3);
    expect(acknowledgements).toBe(0); subscription.update({ channel_ids: ['latest'] });
    await until(() => f.clock.waits.size > 0); f.clock.advanceShort(); await until(() => acknowledgements === 1);
    expect(f.requests[1]!.socket).not.toBe(f.requests[0]!.socket); expect(f.requests[1]!.params).toEqual({ channel_ids: ['latest'] }); await subscription.dispose();
});

test('transient rejection of the last empty set retries without another component update', async () => {
    const f = await setup(); const errors: unknown[] = []; let acknowledgements = 0;
    f.handler = frame => { if (f.requests.length === 2) frame.reject('temporarily_unavailable'); else frame.ack(); };
    const subscription = f.subscription({ onSubscribed: () => { acknowledgements++; }, onError: error => { errors.push(error); } });
    subscription.update({ channel_ids: ['one'] }); await until(() => acknowledgements === 1);
    subscription.update({ channel_ids: [] }); await until(() => errors.length === 1 && [...f.clock.waits].some(wait => wait.milliseconds === 5000));
    f.clock.advanceShort(); await until(() => f.requests.length === 3); expect(f.requests[2]!.params).toEqual({ channel_ids: [] });
    expect(f.sockets).toHaveLength(1); expect(acknowledgements).toBe(1); await subscription.dispose();
});

test('stopping quiesces attempts while disposal drains in-flight ACK and then clears in order', async () => {
    const f = await setup(); f.handler = () => {}; const stopping = new AbortController(); let acknowledged = 0;
    const subscription = f.subscription({ signal: stopping.signal, onSubscribed: () => { acknowledged++; } });
    subscription.update({ channel_ids: ['one'] }); await until(() => f.requests.length === 1); stopping.abort();
    expect(() => subscription.update({ channel_ids: ['after-stop'] })).toThrow(); let disposed = false; const disposal = subscription.dispose().then(() => { disposed = true; });
    await new Promise(resolve => setTimeout(resolve, 10)); expect(disposed).toBe(false); expect(f.requests).toHaveLength(1);
    f.handler = frame => frame.ack(); f.requests[0]!.ack(); await disposal;
    expect(f.requests[1]!.params).toEqual({ channel_ids: [] }); expect(acknowledged).toBe(0); expect(f.sockets[0]!.readyState).toBe(1);
});

test('one cleanup budget bounds a lost in-flight ACK, closes its connection and exposes the timeout', async () => {
    const f = await setup(); f.handler = () => {}; const errors: unknown[] = []; const subscription = f.subscription({ onError: error => { errors.push(error); } });
    subscription.update({ channel_ids: ['one'] }); await until(() => f.requests.length === 1);
    const disposal = subscription.dispose(); f.clock.advanceShort(); await disposal;
    expect(f.requests).toHaveLength(1); expect(f.sockets[0]!.readyState).toBe(3); expect(errors).toHaveLength(1);
    expect(subscription.lastError).toMatchObject({ name: 'TimeoutError' });
});

test('rejected cleanup retires only the connection used by this subscription and releases ownership', async () => {
    const f = await setup(); const errors: unknown[] = [];
    f.handler = frame => { if ((frame.params.channel_ids as unknown[]).length === 0) frame.reject('forbidden'); else frame.ack(); };
    let acknowledged = 0; const subscription = f.subscription({ onSubscribed: () => { acknowledged++; }, onError: error => { errors.push(error); } });
    expect(() => f.subscription()).toThrow('already has an owner'); subscription.update({ channel_ids: ['one'] }); await until(() => acknowledged === 1);
    await subscription.dispose(); expect(f.sockets[0]!.readyState).toBe(3); expect(errors).toHaveLength(1);
    const next = f.subscription(); await next.dispose(); expect(f.requests).toHaveLength(2);
});

test('subscription callbacks may dispose their stream without blocking transport dispatch', async () => {
    const f = await setup(); let disposed = false; let subscription: RelaySubscription;
    subscription = f.subscription({ onSubscribed: async () => { await subscription.dispose(); disposed = true; } });
    subscription.update({ channel_ids: ['one'] }); await until(() => disposed); expect(f.requests[1]!.params).toEqual({ channel_ids: [] });
});

test('actual TLS WebSocket carries replacement ACK, catch-up notification and final clear on the same connection', async () => {
    const material = await tlsMaterial(); let descriptor = signedDescriptor(); const sent: JsonObject[] = [];
    const peer = await startTlsPeer(material, (request, response) => {
        response.setHeader('content-type', 'application/json');
        response.end(JSON.stringify(request.url?.endsWith('relay/descriptor') ? relayDescriptorCodec.encode(descriptor)
            : request.url?.endsWith('auth/challenge') ? { nonce: 'challenge', created_at: Math.floor(Date.now() / 1000), expires_at: Math.floor(Date.now() / 1000) + 300 }
                : { token: 'device', mode: 'device', expires_at: Math.floor(Date.now() / 1000) + 3600 }));
    });
    descriptor = signedDescriptor({ endpoints: [peer.https, peer.wss, signedDescriptor().endpoints[2]!] });
    peer.websocket.on('connection', socket => socket.on('message', data => {
        const request = JSON.parse(String(data)) as { method: string; id: string; params: JsonObject };
        let result: JsonValue = null;
        if (request.method === 'auth.challenge') result = { nonce: 'challenge', created_at: Math.floor(Date.now() / 1000), expires_at: Math.floor(Date.now() / 1000) + 300 };
        else if (request.method === 'auth.device.verify') {
            const proof = deviceAuthenticationCodec.decode(request.params);
            expect(verifyDevice(deviceAuthenticationInput(proof, descriptor.relayId, peer.wss, context), proof.deviceSignature, proof.signerCertificate.signingPublicKey)).toBe(true);
            result = { token: 'device', mode: 'device', expires_at: Math.floor(Date.now() / 1000) + 3600 };
        } else sent.push(request.params);
        socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, result }));
        if (request.method === 'channel.subscribe' && sent.length === 1) socket.send(JSON.stringify({ jsonrpc: '2.0', method: 'channel.timeline.changed', params: { sequence: 7 } }));
    }));
    const registry = new TestRegistry(); registry.entry = { ...registry.entry!, endpoint: peer.https };
    const pool = new RelayClientPool({ context, accountId: certificate.account, fetch: createNodeRelayFetch({ ca: material.ca }), socketFactory: createNodeSocketFactory({ ca: material.ca }) }, registry);
    try {
        const client = await pool.get(descriptor.relayId, identity); let acknowledged = false; let notification = false;
        client.on('notificationReceived', () => { notification = true; });
        const subscription = client.createSubscription('channel.subscribe', { channel_ids: [] }, { onSubscribed: () => { acknowledged = true; } });
        subscription.update({ channel_ids: ['channel-under-test'] }); await until(() => acknowledged && notification); await subscription.dispose();
        expect(sent).toEqual([{ channel_ids: ['channel-under-test'] }, { channel_ids: [] }]); expect(peer.upgrades).toHaveLength(1); expect(peer.failures).toEqual([]);
    } finally { await pool.dispose(); await peer.close(); }
});
