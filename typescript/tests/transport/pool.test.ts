import { expect, test, vi } from 'vitest';
import {
    NetworkContext, RelayClient, RelayAuthenticator, RelayClientPool, RelayError, accountAuthenticationCodec, accountAuthenticationInput, deviceAuthenticationCodec,
    deviceAuthenticationInput, deviceCertificateCodec, decodeBase64Url, relayDescriptorCodec, signAccount, signDevice,
    verifyAccount, verifyDevice, rpcErrorCodes, type AuthenticationIdentity, type JsonObject, type RelayFetch,
} from '../../packages/sdk/src/index.js';
import { createNodeRelayFetch, createNodeSocketFactory } from '../../packages/transport-node/src/index.js';
import { AdvancingClock } from '../support/clock.js';
import { context, signedDescriptor, TestRegistry } from '../support/relay-fixture.js';
import { vector } from '../support/vectors.js';
import { startTlsPeer, tlsMaterial } from '../support/tls-peer.js';

const fixture = vector<{ device_certificate: { unsigned_object: JsonObject; account_signature: string; device_signature: string; private_key: string; device_private_key: string } }>('identity-auth').device_certificate;
const certificate = deviceCertificateCodec.decode({ ...fixture.unsigned_object, account_signature: fixture.account_signature, device_signature: fixture.device_signature });
const accountIdentity: AuthenticationIdentity = { mode: 'account', signer: { accountId: certificate.account, publicKey: certificate.accountPublicKey,
    sign: async input => signAccount(input, decodeBase64Url(fixture.private_key)) } };
const deviceIdentity: AuthenticationIdentity = { mode: 'device', signer: { certificate, sign: async input => signDevice(input, decodeBase64Url(fixture.device_private_key)) } };
const accountId = certificate.account;
function result(body: unknown, status = 200): Response { return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }); }

function fakeRelay() {
    const clock = new AdvancingClock();
    const registry = new TestRegistry();
    const descriptor = signedDescriptor();
    const methods: string[] = [];
    let rejectBusiness: string | undefined;
    let token = 0;
    const fetch: RelayFetch = async (url, init) => {
        const method = url.slice(url.includes('bootstrap') ? url.indexOf('/', 8) + 1 : url.indexOf('/v1/') + 4).replaceAll('/', '.');
        methods.push(method);
        if (method === 'relay.descriptor') return result(relayDescriptorCodec.encode(descriptor));
        if (method === 'auth.challenge') return result({ nonce: 'test-challenge', created_at: clock.wall, expires_at: clock.wall + 300 });
        if (method === 'auth.account.verify') {
            const proof = accountAuthenticationCodec.parse(init.body!);
            expect(verifyAccount(accountAuthenticationInput(proof, accountId, descriptor.relayId, url, context), proof.accountSignature, proof.accountPublicKey)).toBe(true);
            return result({ token: `account-${++token}`, mode: 'account', expires_at: clock.wall + 3600 });
        }
        if (method === 'auth.device.verify') {
            const proof = deviceAuthenticationCodec.parse(init.body!);
            expect(verifyDevice(deviceAuthenticationInput(proof, descriptor.relayId, url, context), proof.deviceSignature, proof.signerCertificate.signingPublicKey)).toBe(true);
            return result({ token: `device-${++token}`, mode: 'device', expires_at: clock.wall + 3600 });
        }
        if (rejectBusiness) { const code = rejectBusiness; rejectBusiness = undefined; return result({ code, message: 'rejected by relay' }, code === 'unauthorized' ? 401 : 403); }
        return result({ token: init.headers['X-Meshline-Session'] ?? null });
    };
    return { clock, registry, descriptor, methods, fetch, rejectOnce(code = 'unauthorized') { rejectBusiness = code; } };
}

test('pool constructors do no I/O and concurrent acquisitions authenticate only once', async () => {
    const relay = fakeRelay();
    const pool = new RelayClientPool({ context, accountId, clock: relay.clock, fetch: relay.fetch }, relay.registry);
    expect(relay.methods).toEqual([]); expect(relay.registry.reads).toBe(0);
    const clients = await Promise.all(Array.from({ length: 8 }, () => pool.get(relay.descriptor.relayId, accountIdentity)));
    expect(new Set(clients).size).toBe(1); expect(pool.clients).toHaveLength(1);
    expect(relay.methods).toEqual(['relay.descriptor', 'auth.challenge', 'auth.account.verify']);
    expect(clients[0]!.state).toMatchObject({ authentication: 'account', sessionMode: 'account', connection: 'connected' });
    await pool.dispose(); expect(clients[0]!.isDisposed).toBe(true);
});

test('account and device clients use separate tokens and invalidation only retires device clients', async () => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    const account = await pool.get(relay.descriptor.relayId, accountIdentity);
    const device = await pool.get(relay.descriptor.relayId, deviceIdentity);
    expect(account).not.toBe(device); expect(pool.clients).toHaveLength(2);
    expect(await account.requestHttp('POST', 'test.probe')).toEqual({ token: 'account-1' });
    expect(await device.requestHttp('POST', 'test.probe')).toEqual({ token: 'device-2' });
    await pool.invalidateDevice();
    expect(device.isDisposed).toBe(true); expect(account.isDisposed).toBe(false); expect(pool.clients).toEqual([account]);
    const replacement = await pool.get(relay.descriptor.relayId, deviceIdentity);
    expect(replacement).not.toBe(device); await pool.dispose();
});

test('unauthorized business response is returned without replay and the next call refreshes', async () => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    const client = await pool.get(relay.descriptor.relayId, accountIdentity);
    const errors: unknown[] = []; client.on('errorOccurred', error => { errors.push(error); });
    relay.rejectOnce();
    await expect(client.requestHttp('POST', 'message.send', {})).rejects.toBeInstanceOf(RelayError);
    expect(relay.methods.filter(method => method === 'message.send')).toHaveLength(1);
    expect(errors).toHaveLength(1);
    expect(await client.requestHttp('POST', 'message.send', {})).toEqual({ token: 'account-2' });
    expect(relay.methods.filter(method => method === 'auth.challenge')).toHaveLength(2);
    await pool.dispose();
});

test.each(['unauthorized', 'device_unknown', 'invalid_signature'])('HTTP authentication records %s as rejected and recovers after a new proof', async code => {
    const relay = fakeRelay(); let reject = true;
    const fetch: RelayFetch = (url, init) => url.endsWith('/auth/device/verify') && reject ? Promise.resolve(result({ code, message: 'proof rejected' }, 403)) : relay.fetch(url, init);
    const client = new RelayClient(relay.descriptor.relayId, relay.registry, { fetch, clock: relay.clock }, new RelayAuthenticator(relay.descriptor.relayId, context, deviceIdentity, relay.clock));
    const states: string[] = []; client.on('stateChanged', state => { states.push(state.authentication); });
    try {
        await expect(client.authenticate()).rejects.toMatchObject({ code }); expect(client.state).toMatchObject({ authentication: 'rejected', lastError: { code } });
        expect(states).toContain('rejected'); reject = false; await client.authenticate();
        expect(client.state).toMatchObject({ authentication: 'device', lastError: undefined });
    } finally { await client.dispose(); }
    expect(client.state.authentication).toBe('none');
});

test.each(['unauthorized', 'device_unknown', 'invalid_signature'])('HTTP %s remains observable despite a cached session and failed error observer', async code => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    const client = await pool.get(relay.descriptor.relayId, accountIdentity); const detach = client.on('errorOccurred', async () => { throw new Error('diagnostic callback'); });
    try {
        relay.rejectOnce(code); await expect(client.requestHttp('POST', 'test.probe')).rejects.toMatchObject({ code });
        await vi.waitFor(() => expect(client.state.lastError).toBeInstanceOf(AggregateError)); expect(client.state.authentication).toBe('rejected');
        expect(relay.methods.filter(method => method === 'test.probe')).toHaveLength(1);
        await client.requestHttp('GET', 'test.public', undefined, { authenticated: false }); expect(client.state.authentication).toBe('rejected');
        detach(); relay.clock.elapsed += 3600000; await client.requestHttp('POST', 'test.probe');
        expect(client.state.authentication).toBe('account'); expect(relay.methods.filter(method => method === 'auth.account.verify')).toHaveLength(2);
    } finally { detach(); await pool.dispose(); }
});

test('business permission errors do not label an authenticated session as rejected', async () => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    const client = await pool.get(relay.descriptor.relayId, accountIdentity);
    try { relay.rejectOnce('forbidden'); await expect(client.requestHttp('POST', 'test.probe')).rejects.toMatchObject({ code: 'forbidden' }); expect(client.state.authentication).toBe('account'); }
    finally { await pool.dispose(); }
});

test('an observer throwing an authentication-shaped error does not reject a healthy session', async () => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    const client = await pool.get(relay.descriptor.relayId, accountIdentity);
    const detach = client.on('stateChanged', () => { throw new RelayError({ code: 'invalid_signature', message: 'application observer' }); });
    try { await client.requestHttp('POST', 'test.probe'); expect(client.state.authentication).toBe('account'); expect(client.state.lastError).toBeInstanceOf(AggregateError); }
    finally { detach(); await pool.dispose(); }
});

test.each(['unauthorized', 'device_unknown', 'invalid_signature'])('real WSS %s cannot be hidden by a valid HTTP session and reconnect can authenticate again', async code => {
    const material = await tlsMaterial(); let descriptor = signedDescriptor(); let reject = true; let proofs = 0; let probes = 0;
    const peer = await startTlsPeer(material, (request, response) => {
        const now = Math.floor(Date.now() / 1000); response.setHeader('Content-Type', 'application/json');
        if (request.url === '/relay/descriptor') response.end(relayDescriptorCodec.stringify(descriptor));
        else if (request.url === '/auth/challenge') response.end(JSON.stringify({ nonce: 'challenge', created_at: now, expires_at: now + 300 }));
        else response.end(JSON.stringify({ token: 'http-account', mode: 'account', expires_at: now + 3600 }));
    });
    descriptor = signedDescriptor({ endpoints: [peer.https, peer.wss, descriptor.endpoints[2]!] });
    peer.websocket.on('connection', socket => socket.on('message', data => {
        const request = JSON.parse(String(data)) as { id: string; method: string }; const now = Math.floor(Date.now() / 1000);
        if (request.method === 'auth.account.verify') {
            proofs++;
            if (reject) { socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: rpcErrorCodes[code], message: 'proof rejected' } })); return; }
        }
        if (request.method === 'test.probe') probes++;
        socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: request.method === 'auth.challenge' ? { nonce: 'challenge', created_at: now, expires_at: now + 300 }
            : request.method === 'auth.account.verify' ? { token: 'socket-account', mode: 'account', expires_at: now + 3600 } : { ok: true } }));
    }));
    const registry = new TestRegistry(); registry.entry = { ...registry.entry!, endpoint: peer.https };
    const pool = new RelayClientPool({ context, accountId, fetch: createNodeRelayFetch({ ca: material.ca }), socketFactory: createNodeSocketFactory({ ca: material.ca }) }, registry);
    try {
        const client = await pool.get(descriptor.relayId, accountIdentity); expect(client.state.authentication).toBe('account');
        await expect(client.requestWebSocket('test.probe')).rejects.toMatchObject({ code });
        expect(client.state.authentication).toBe('rejected'); expect(proofs).toBe(1); expect(probes).toBe(0);
        reject = false; await expect(client.requestWebSocket('test.probe')).resolves.toEqual({ ok: true });
        expect(client.state.authentication).toBe('account'); expect(proofs).toBe(2); expect(probes).toBe(1);
    } finally { await pool.dispose(); await peer.close(); }
});

test.each(['device_unknown', 'invalid_signature'])('a WSS renewal rejected with %s retains the accepted session until expiry', async code => {
    const material = await tlsMaterial(); const clock = new AdvancingClock(); let resume: (() => void) | undefined;
    vi.spyOn(clock, 'delay').mockImplementation((_milliseconds, signal) => new Promise<void>((resolve, reject) => {
        const cleanup = () => { resume = undefined; signal?.removeEventListener('abort', abort); };
        const abort = () => { cleanup(); reject(signal!.reason); };
        resume = () => { cleanup(); resolve(); }; signal?.addEventListener('abort', abort, { once: true });
        if (signal?.aborted) abort();
    }));
    let descriptor = signedDescriptor(); let proofs = 0;
    const peer = await startTlsPeer(material, (request, response) => {
        response.setHeader('Content-Type', 'application/json');
        if (request.url === '/relay/descriptor') response.end(relayDescriptorCodec.stringify(descriptor));
        else if (request.url === '/auth/challenge') response.end(JSON.stringify({ nonce: 'challenge', created_at: clock.wall, expires_at: clock.wall + 300 }));
        else response.end(JSON.stringify({ token: 'http-account', mode: 'account', expires_at: clock.wall + 3600 }));
    });
    descriptor = signedDescriptor({ endpoints: [peer.https, peer.wss, descriptor.endpoints[2]!] });
    peer.websocket.on('connection', socket => socket.on('message', data => {
        const request = JSON.parse(String(data)) as { id: string; method: string };
        if (request.method === 'auth.account.verify' && ++proofs === 2) {
            socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: rpcErrorCodes[code], message: 'renewal rejected' } })); return;
        }
        socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: request.method === 'auth.challenge' ? { nonce: 'challenge', created_at: clock.wall, expires_at: clock.wall + 300 }
            : request.method === 'auth.account.verify' ? { token: 'socket-account', mode: 'account', expires_at: clock.wall + 121 } : { ok: true } }));
    }));
    const registry = new TestRegistry(); registry.entry = { ...registry.entry!, endpoint: peer.https };
    const pool = new RelayClientPool({ context, accountId, clock, fetch: createNodeRelayFetch({ ca: material.ca }), socketFactory: createNodeSocketFactory({ ca: material.ca }) }, registry);
    try {
        const client = await pool.get(descriptor.relayId, accountIdentity); const errors: unknown[] = [];
        client.on('errorOccurred', value => { errors.push(value); }); await client.requestWebSocket('test.probe');
        expect(resume).toBeDefined(); clock.elapsed += 96000; clock.wall += 96; resume!();
        await vi.waitFor(() => expect(errors).toHaveLength(1)); expect(client.state).toMatchObject({ authentication: 'account', lastError: { code } });
        expect(await client.requestWebSocket('test.probe')).toEqual({ ok: true }); expect(peer.upgrades).toHaveLength(1); expect(proofs).toBe(2);
    } finally { await pool.dispose(); await peer.close(); }
});

test('monotonic expiry refreshes before business send even after local wall time moves backward', async () => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    const client = await pool.get(relay.descriptor.relayId, accountIdentity);
    relay.clock.elapsed += 3600000; relay.clock.wall -= 100000;
    expect(client.state.authentication).toBe('expired');
    expect(await client.requestHttp('POST', 'test.probe')).toEqual({ token: 'account-2' });
    await pool.dispose();
});

test('pool rejects context/account/device rebinding before network I/O', async () => {
    const relay = fakeRelay();
    expect(() => new RelayClientPool({ context: new NetworkContext(1, context.registry), accountId }, relay.registry)).toThrow('context');
    const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    await expect(pool.get(relay.descriptor.relayId, { mode: 'account', signer: { accountId: 'other', publicKey: certificate.accountPublicKey, sign: async () => new Uint8Array(64) } })).rejects.toThrow('account');
    expect(relay.methods).toEqual([]);
    await pool.get(relay.descriptor.relayId, deviceIdentity);
    await expect(pool.get(relay.descriptor.relayId, { mode: 'device', signer: { certificate: { ...certificate, signingPublicKey: new Uint8Array(32).fill(3) }, sign: async () => new Uint8Array(64) } })).rejects.toThrow('device');
    await pool.dispose();
});

test.each(['poolChanged', 'relayChanged'] as const)('%s observers may await the same pool disposal', async event => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    await pool.get(relay.descriptor.relayId, deviceIdentity); let release!: () => void;
    const escape = new Promise<void>(resolve => { release = resolve; }); let observed = false; let completed = false;
    pool.on(event, async () => { observed = true; await Promise.race([pool.dispose(), escape]); });
    const disposal = pool.dispose().then(() => { completed = true; });
    try { await vi.waitFor(() => expect(completed).toBe(true), { timeout: 1000 }); expect(observed).toBe(true); }
    finally { release(); await disposal; }
});

test('pool observers cannot defer acquisition, skip authentication on reentry or conceal another failure', async () => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    let release!: () => void; const pending = new Promise<void>(resolve => { release = resolve; }); const modes: unknown[] = []; const failures: unknown[] = [];
    const observer = new Error('pool observer'); const errorObserver = new Error('pool error observer');
    pool.on('poolChanged', () => pending);
    pool.on('poolChanged', async clients => { if (clients.length) modes.push((await pool.get(relay.descriptor.relayId, deviceIdentity)).state.authentication); });
    pool.on('poolChanged', async () => { throw observer; });
    pool.on('errorOccurred', failure => { failures.push(failure); });
    pool.on('errorOccurred', async () => { throw errorObserver; });
    try {
        const client = await pool.get(relay.descriptor.relayId, deviceIdentity);
        await vi.waitFor(() => expect(modes).toEqual(['device'])); expect(client.state.authentication).toBe('device');
        expect(relay.methods.filter(method => method === 'auth.device.verify')).toHaveLength(1);
        await vi.waitFor(() => expect(pool.lastError).toMatchObject({ errors: [expect.objectContaining({ errors: [observer] }), expect.objectContaining({ errors: [errorObserver] })] }));
        expect(failures).toHaveLength(1);
        await pool.dispose(); expect(client.isDisposed).toBe(true);
    } finally { release(); await pool.dispose(); }
});

test('state and error observers cannot reject successful HTTP work or recursively notify themselves', async () => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    const client = await pool.get(relay.descriptor.relayId, accountIdentity); let states = 0;
    const observer = new Error('state observer'); const errorObserver = new Error('relay error observer'); const failures: unknown[] = [];
    client.on('stateChanged', () => { states++; throw observer; });
    client.on('errorOccurred', error => { failures.push(error); });
    client.on('errorOccurred', async () => { throw errorObserver; });
    try {
        expect(await client.requestHttp('POST', 'test.probe')).toEqual({ token: 'account-1' });
        await vi.waitFor(() => expect(client.state.lastError).toMatchObject({ errors: [expect.objectContaining({ errors: [observer] }), expect.objectContaining({ errors: [errorObserver] })] }));
        expect(states).toBe(1); expect(failures).toHaveLength(1); expect(pool.clients).toEqual([client]);
    } finally { await pool.dispose(); }
});

test('an asynchronous error-observer failure retains the original relay rejection', async () => {
    const relay = fakeRelay(); const pool = new RelayClientPool({ context, accountId, fetch: relay.fetch, clock: relay.clock }, relay.registry);
    const client = await pool.get(relay.descriptor.relayId, accountIdentity); const observer = new Error('diagnostic observer');
    client.on('errorOccurred', async () => { throw observer; }); relay.rejectOnce();
    try {
        const original = await client.requestHttp('POST', 'message.send').catch(error => error);
        expect(original).toBeInstanceOf(RelayError);
        await vi.waitFor(() => expect(client.state.lastError).toMatchObject({ errors: [original, expect.objectContaining({ errors: [observer] })] }));
        expect(relay.methods.filter(method => method === 'message.send')).toHaveLength(1);
    } finally { await pool.dispose(); }
});

async function notificationRelay(malformed = false) {
    const material = await tlsMaterial(); let descriptor = signedDescriptor();
    const peer = await startTlsPeer(material, (request, response) => {
        const now = Math.floor(Date.now() / 1000); response.setHeader('Content-Type', 'application/json');
        if (request.url === '/relay/descriptor') response.end(relayDescriptorCodec.stringify(descriptor));
        else if (request.url === '/auth/challenge') response.end(JSON.stringify({ nonce: 'challenge', created_at: now, expires_at: now + 300 }));
        else response.end(JSON.stringify({ token: 'http-device', mode: 'device', expires_at: now + 3600 }));
    });
    descriptor = signedDescriptor({ endpoints: [peer.https, peer.wss, descriptor.endpoints[2]!] });
    peer.websocket.on('connection', socket => socket.on('message', data => {
        const request = JSON.parse(String(data)) as { id: string; method: string }; const now = Math.floor(Date.now() / 1000);
        socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: request.method === 'auth.challenge'
            ? { nonce: 'challenge', created_at: now, expires_at: now + 300 }
            : request.method === 'auth.device.verify' ? { token: 'socket-device', mode: 'device', expires_at: now + 3600 } : { ok: true } }));
        if (request.method === 'auth.device.verify') socket.send(malformed ? '{invalid' : JSON.stringify({ jsonrpc: '2.0', method: 'message.timeline.changed', params: { sequence: 1 } }));
    }));
    const registry = new TestRegistry(); registry.entry = { ...registry.entry!, endpoint: peer.https };
    const pool = new RelayClientPool({ context, accountId, fetch: createNodeRelayFetch({ ca: material.ca }), socketFactory: createNodeSocketFactory({ ca: material.ca }) }, registry);
    const client = await pool.get(descriptor.relayId, deviceIdentity);
    return { pool, client, peer };
}

test.each(['socketConnected', 'notificationReceived', 'errorOccurred', 'faulted'] as const)('a %s observer can dispose its owning pool', async event => {
    const { pool, client, peer } = await notificationRelay(event === 'errorOccurred' || event === 'faulted');
    let release!: () => void; const escape = new Promise<void>(resolve => { release = resolve; }); let completed = false; let disposal: Promise<void> | undefined;
    client.on(event, async () => { disposal ??= pool.dispose().then(() => { completed = true; }); await Promise.race([disposal, escape]); });
    client.startNotifications();
    try { await vi.waitFor(() => expect(completed).toBe(true), { timeout: 1500 }); expect(client.isDisposed).toBe(true); }
    finally { release(); await disposal; await pool.dispose(); await peer.close(); }
});

test('one pending or failing notification observer cannot block later observers or retire a healthy WSS session', async () => {
    const { pool, client, peer } = await notificationRelay(); let release!: () => void;
    const pending = new Promise<void>(resolve => { release = resolve; }); const received: unknown[] = []; const failures: unknown[] = [];
    const observer = new Error('notification observer');
    client.on('notificationReceived', () => pending);
    client.on('notificationReceived', async () => { throw observer; });
    client.on('notificationReceived', value => { received.push(value); });
    client.on('errorOccurred', value => { failures.push(value); });
    try {
        client.startNotifications(); await vi.waitFor(() => expect(received).toHaveLength(1));
        await vi.waitFor(() => expect(client.state.lastError).toMatchObject({ errors: [observer] }));
        expect(failures).toHaveLength(1); expect(pool.clients).toEqual([client]);
        expect(await client.requestWebSocket('test.probe')).toEqual({ ok: true }); expect(peer.upgrades).toHaveLength(1);
        await pool.dispose(); expect(client.isDisposed).toBe(true);
    } finally { release(); await pool.dispose(); await peer.close(); }
});

test('real WSS pool renews one device session without reconnecting or losing immediate notifications', async () => {
    const material = await tlsMaterial();
    let descriptor = signedDescriptor(); let verifies = 0;
    const peer = await startTlsPeer(material, (request, response) => {
        response.setHeader('Content-Type', 'application/json');
        if (request.url === '/relay/descriptor') response.end(relayDescriptorCodec.stringify(descriptor));
        else if (request.url === '/auth/challenge') response.end(JSON.stringify({ nonce: 'challenge', created_at: Math.floor(Date.now() / 1000), expires_at: Math.floor(Date.now() / 1000) + 300 }));
        else response.end(JSON.stringify({ token: 'http-device', mode: 'device', expires_at: Math.floor(Date.now() / 1000) + 3600 }));
    });
    descriptor = signedDescriptor({ endpoints: [peer.https, peer.wss, descriptor.endpoints[2]!] });
    peer.websocket.on('connection', socket => socket.on('message', data => {
        const request = JSON.parse(String(data)) as { id: string; method: string; params: JsonObject };
        if (request.method === 'auth.challenge') socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: { nonce: 'challenge', created_at: Math.floor(Date.now() / 1000), expires_at: Math.floor(Date.now() / 1000) + 300 } }));
        else if (request.method === 'auth.device.verify') {
            const proof = deviceAuthenticationCodec.decode(request.params);
            expect(verifyDevice(deviceAuthenticationInput(proof, descriptor.relayId, peer.wss, context), proof.deviceSignature, proof.signerCertificate.signingPublicKey)).toBe(true);
            verifies++;
            socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: { token: 'socket-device', mode: 'device', expires_at: Math.floor(Date.now() / 1000) + 3 } }));
            socket.send(JSON.stringify({ jsonrpc: '2.0', method: 'message.timeline.changed', params: { sequence: verifies } }));
        } else socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: { ok: true } }));
    }));
    const registry = new TestRegistry(); registry.entry = { ...registry.entry!, endpoint: peer.https };
    const pool = new RelayClientPool({ context, accountId, fetch: createNodeRelayFetch({ ca: material.ca }), socketFactory: createNodeSocketFactory({ ca: material.ca }) }, registry);
    try {
        const client = await pool.get(descriptor.relayId, deviceIdentity);
        const notifications: unknown[] = []; const errors: unknown[] = [];
        client.on('notificationReceived', notification => { notifications.push(notification); });
        client.on('errorOccurred', error => { errors.push(error); });
        expect(await client.requestWebSocket('test.probe', {})).toEqual({ ok: true });
        await vi.waitFor(() => expect(verifies).toBeGreaterThanOrEqual(2), { timeout: 4500, interval: 50 });
        await vi.waitFor(() => expect(notifications).toHaveLength(verifies));
        expect(peer.upgrades).toHaveLength(1); expect(errors).toEqual([]);
        expect(client.state.authentication).toBe('device');
    } finally { await pool.dispose(); await peer.close(); }
});
