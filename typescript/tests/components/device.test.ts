import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test, vi } from 'vitest';
import {
    AccountManager, DeviceManager, RelayClientPool, accountDeviceStateCodec, accountPublicKey, accountRouteCodec, agreeKey,
    certificateId, concatBytes, decryptAes, deviceStateInput, encryptAes, encryptionPublicKey, encodeUtf8, getAccountId,
    relayDescriptorCodec, routeAccountInput, routeRelayInput, signAccount, systemRandom, verifyDevice,
    contactGrantInput, contactInviteInput, deviceCertificateCodec, deviceStateQueryInput, decodeBase64Url, signedDeviceStateQueryCodec, signDevice, validateDeviceStateQuery,
    type AccountDeviceState, type AccountRoute, type RelayFetch, type SecretProtector, type RandomSource, type RelaySocket, type RelaySocketEvents,
} from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { context, relayPrivateKey, signedDescriptor, TestRegistry } from '../support/relay-fixture.js';
import { AdvancingClock } from '../support/clock.js';
import { removeTestDirectory } from '../support/temp.js';
import { vector } from '../support/vectors.js';

const accountKey = new Uint8Array(32).fill(3); const accountId = getAccountId('neo:860833102', accountPublicKey(accountKey));
const resources: { dispose(): Promise<void> }[] = []; const directories: string[] = [];
afterEach(async () => { for (const resource of resources.splice(0).reverse()) await resource.dispose(); for (const path of directories.splice(0)) await removeTestDirectory(path); });
class Protector implements SecretProtector {
    readonly master = new Uint8Array(32).fill(9); readonly purposes: string[] = []; readonly inputs: Uint8Array[] = []; readonly recovered: Uint8Array[] = [];
    failProtect = false; wrongKey = false;
    async protect(bytes: Uint8Array, purpose: string) {
        this.inputs.push(bytes); this.purposes.push(purpose); if (this.failProtect) throw new Error('Keychain unavailable');
        const nonce = systemRandom.bytes(12); return concatBytes(nonce, encryptAes(this.master, nonce, bytes, encodeUtf8(purpose)));
    }
    async unprotect(bytes: Uint8Array, purpose: string) {
        this.purposes.push(purpose);
        const key = this.wrongKey ? new Uint8Array(32).fill(21) : decryptAes(this.master, bytes.slice(0, 12), bytes.slice(12), encodeUtf8(purpose));
        this.recovered.push(key); return key;
    }
}
function response(body: unknown, status = 200): Response { return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }); }
class RuntimeSocket implements RelaySocket {
    readyState = 0; bufferedAmount = 0; authenticated = false;
    readonly listeners = new Map<string, Set<(value: never) => void>>();
    constructor(readonly clock: AdvancingClock) { queueMicrotask(() => { this.readyState = 1; this.emit('open', undefined); }); }
    on<K extends keyof RelaySocketEvents>(event: K, listener: (value: RelaySocketEvents[K]) => void): () => void {
        const set = this.listeners.get(event) ?? new Set(); this.listeners.set(event, set); set.add(listener as (value: never) => void);
        return () => { set.delete(listener as (value: never) => void); };
    }
    emit<K extends keyof RelaySocketEvents>(event: K, value: RelaySocketEvents[K]): void { for (const listener of this.listeners.get(event) ?? []) listener(value as never); }
    close(code: number): number { this.readyState = 3; this.emit('close', { code, reason: '' }); return code; }
    send(text: string): void {
        const request = JSON.parse(text) as { id: string; method: string };
        const result = request.method === 'auth.challenge' ? { nonce: 'challenge', created_at: this.clock.wall, expires_at: this.clock.wall + 300 }
            : { token: 'socket-device', mode: 'device', expires_at: this.clock.wall + 3600 };
        queueMicrotask(() => { this.emit('message', JSON.stringify({ jsonrpc: '2.0', id: request.id, result })); if (request.method === 'auth.device.verify') this.authenticated = true; });
    }
    notify(revision: number): void { this.emit('message', JSON.stringify({ jsonrpc: '2.0', method: 'device.state.changed', params: { revision } })); }
}
function controlDelays(clock: AdvancingClock) {
    const waits = new Set<{ milliseconds: number; complete(): void }>();
    clock.delay = (milliseconds, signal) => new Promise<void>((resolve, reject) => {
        signal?.throwIfAborted();
        const aborted = (): void => { waits.delete(wait); reject(signal!.reason); };
        const wait = { milliseconds, complete() { waits.delete(wait); signal?.removeEventListener('abort', aborted); resolve(); } };
        waits.add(wait); signal?.addEventListener('abort', aborted, { once: true });
    });
    return { waits, advance(milliseconds: number) { for (const wait of [...waits]) if (wait.milliseconds === milliseconds) wait.complete(); } };
}
async function fixture(path?: string, protector = new Protector(), random: RandomSource = systemRandom) {
    if (!path) { const directory = await mkdtemp(join(tmpdir(), 'meshline-device-')); directories.push(directory); path = join(directory, 'state.sqlite'); }
    const store = new NodeSqliteStore(path); resources.push(store); await store.migrate();
    const descriptor = signedDescriptor(); const clock = new AdvancingClock(); const requests: string[] = []; const sockets: RuntimeSocket[] = [];
    let state: AccountDeviceState | undefined; let behavior: 'accepted' | 'staged' | 'lost' | 'bad-staging' | 'reject' = 'accepted'; let deviceDenied = false;
    const modes: string[] = [];
    let route: AccountRoute = { account: accountId, accountPublicKey: accountPublicKey(accountKey), revision: 0, relayId: descriptor.relayId,
        updatedAt: clock.wall, expiresAt: clock.wall + 315360000, accountSignature: new Uint8Array(64) };
    route = { ...route, accountSignature: signAccount(routeAccountInput(route, context), accountKey) };
    route = { ...route, relaySignature: signAccount(routeRelayInput(route, context), relayPrivateKey) };
    const fetch: RelayFetch = async (url, init) => {
        const name = new URL(url).pathname.replace(/^\/v1/, '').slice(1).replaceAll('/', '.');
        if (name === 'relay.descriptor') return response(relayDescriptorCodec.encode(descriptor));
        if (name === 'auth.challenge') return response({ nonce: 'challenge', created_at: clock.wall, expires_at: clock.wall + 300 });
        if (name === 'auth.account.verify' || name === 'auth.device.verify') { const mode = name.includes('account') ? 'account' : 'device'; return response({ token: mode, mode, expires_at: clock.wall + 3600 }); }
        if (name === 'account.route.resolve') return response(accountRouteCodec.encode(route));
        if (name === 'device.state.resolve') {
            if (init.method === 'POST') validateDeviceStateQuery(signedDeviceStateQueryCodec.parse(init.body!), context, clock.wall);
            const mode = init.headers['X-Meshline-Session']!; modes.push(mode);
            if (mode === 'device' && deviceDenied) return response({ code: 'device_unknown', message: 'Revoked' }, 403);
            return state ? response(accountDeviceStateCodec.encode(state)) : response({ code: 'not_found', message: 'No state' }, 404);
        }
        if (name !== 'device.state.publish') throw new Error(`Unexpected method ${name}`);
        requests.push(init.body!); expect(init.headers['X-Meshline-Session']).toBe('account');
        expect((await store.read([{ collection: 'signed_requests', key: name }])).sets[0]![0]!.value.pending).toBe(true);
        if (behavior === 'reject') return response({ code: 'forbidden', message: 'Rejected' }, 403);
        if (behavior === 'staged' || behavior === 'bad-staging') return response({ status: 'staged', staged_until: clock.wall + (behavior === 'staged' ? 300 : -1) });
        state = accountDeviceStateCodec.parse(init.body!);
        if (behavior === 'lost') throw new TypeError('Device state accepted but response was lost');
        return response({ status: 'accepted' });
    };
    const signer = { accountId, publicKey: accountPublicKey(accountKey), sign: async (input: Uint8Array) => signAccount(input, accountKey) };
    const pool = new RelayClientPool({ context, accountId, clock, fetch, socketFactory: () => { const socket = new RuntimeSocket(clock); sockets.push(socket); return socket; } }, new TestRegistry()); resources.push(pool);
    const account = new AccountManager({ context, accountId, store, relayClients: pool, accountSigner: signer, clock }); resources.push(account); await account.initialize();
    const manager = new DeviceManager({ context, accountId, store, relayClients: pool, accountManager: account, accountSigner: signer, secretProtector: protector, random, clock }); resources.push(manager);
    return { store, pool, account, manager, protector, clock, descriptor, requests, modes, sockets,
        set behavior(value: typeof behavior) { behavior = value; }, set deviceDenied(value: boolean) { deviceDenied = value; },
        set state(value: AccountDeviceState | undefined) { state = value; }, get state() { return state; },
        async pending() { return (await store.read([{ collection: 'signed_requests', key: 'device.state.publish' }])).sets[0]![0]?.value; },
        signState(value: AccountDeviceState) { return { ...value, accountSignature: signAccount(deviceStateInput(value, context), accountKey) }; },
    };
}

test('device creation atomically binds protected keys, signs and agrees, renews without changing identity', async () => {
    const f = await fixture(); expect(f.manager.local).toBeUndefined(); await f.manager.initialize();
    const certificate = await f.manager.createDevice(3600); const id = certificateId(certificate, context);
    expect(f.protector.purposes).toEqual([`Meshline/device-signing/v1/${context}/${accountId}/${id}`, `Meshline/device-encryption/v1/${context}/${accountId}/${id}`]);
    expect(f.protector.inputs.every(input => input.every(byte => byte === 0))).toBe(true);
    const snapshot = await f.store.read([{ collection: 'local_device' }, { collection: 'identity_binding' }]);
    expect(snapshot.sets[0]![0]!.revision).toBe(snapshot.sets[1]![0]!.revision);
    const input = encodeUtf8('test signing'); const signature = await f.manager.sign(input); expect(verifyDevice(input, signature, certificate.signingPublicKey)).toBe(true);
    const peerKey = new Uint8Array(32).fill(8); expect(await f.manager.deriveSharedSecret(encryptionPublicKey(peerKey))).toEqual(agreeKey(peerKey, certificate.encryptionPublicKey));
    await expect(f.manager.deriveSharedSecret(new Uint8Array(32))).rejects.toThrow();
    f.clock.wall += 100; const renewed = await f.manager.renewDevice(7200); expect(certificateId(renewed, context)).toBe(id); expect(renewed.expiresAt).toBe(f.clock.wall + 7200);
    expect(f.requests).toEqual([]); expect(f.manager.deviceState).toBeUndefined();
    await expect(f.manager.createDevice(3600)).rejects.toThrow('already exists');
});

test('restart lazily unprotects keys, validates key-to-certificate binding and clears cache on disposal', async () => {
    const first = await fixture(); await first.manager.initialize(); const certificate = await first.manager.createDevice(3600);
    await first.manager.dispose(); await first.store.dispose();
    const protector = new Protector(); const resumed = await fixture(first.store.path, protector); await resumed.manager.initialize(); expect(protector.recovered).toEqual([]);
    const input = encodeUtf8('restart'); expect(verifyDevice(input, await resumed.manager.sign(input), certificate.signingPublicKey)).toBe(true);
    await resumed.manager.sign(input); expect(protector.recovered).toHaveLength(1); expect(protector.recovered[0]!.some(value => value !== 0)).toBe(true);
    await resumed.manager.dispose(); expect(protector.recovered[0]!.every(value => value === 0)).toBe(true);
    const wrong = new Protector(); wrong.wrongKey = true; const invalid = await fixture(first.store.path, wrong); await invalid.manager.initialize();
    await expect(invalid.manager.sign(input)).rejects.toThrow('does not match'); expect(wrong.recovered[0]!.every(value => value === 0)).toBe(true);
});

test('protection failure leaves no local binding and erases generated secrets', async () => {
    const keys: Uint8Array[] = []; const protector = new Protector(); protector.failProtect = true;
    const f = await fixture(undefined, protector, { bytes: n => { const key = systemRandom.bytes(n); keys.push(key); return key; } }); await f.manager.initialize();
    await expect(f.manager.createDevice(3600)).rejects.toThrow('Keychain unavailable');
    expect(keys.every(key => key.every(byte => byte === 0))).toBe(true);
    expect((await f.store.read([{ collection: 'local_device' }, { collection: 'identity_binding' }])).sets).toEqual([[], []]);
    expect(f.manager.local).toBeUndefined();
});

test('partial/tampered persisted bindings fail initialization instead of generating another identity', async () => {
    const f = await fixture(); await f.manager.initialize(); await f.manager.createDevice(3600); await f.manager.dispose();
    await f.store.commit((await f.store.read([])).version, [{ collection: 'identity_binding', key: 'device', kind: 'delete' }]);
    const resumed = await fixture(f.store.path); await expect(resumed.manager.initialize()).rejects.toThrow('must exist together');
    expect(resumed.manager.lifecycleState).toBe('uninitialized');
});

test('staged publication settles delivery without declaring devices authoritative; acceptance publishes state atomically', async () => {
    const f = await fixture(); await f.manager.initialize(); const certificate = await f.manager.createDevice(3600); const id = certificateId(certificate, context);
    f.behavior = 'staged'; const staged = await f.manager.publishDeviceState(f.descriptor.relayId, { certificates: [certificate] });
    expect(staged.status).toBe('staged'); expect(f.manager.deviceState).toBeUndefined(); expect(f.manager.getAuthorizationState(id)).toBe('unknown'); expect((await f.pending())!.pending).toBe(false);
    f.behavior = 'accepted'; const accepted = await f.manager.publishDeviceState(f.descriptor.relayId);
    expect(accepted.deviceState.revision).toBe(staged.deviceState.revision + 1); expect(f.manager.getAuthorizationState(id)).toBe('authorized');
    const snapshot = await f.store.read([{ collection: 'device_states' }, { collection: 'signed_requests' }]);
    expect(snapshot.sets[0]![0]!.revision).toBe(snapshot.sets[1]![0]!.revision);
    f.clock.wall = certificate.expiresAt; expect(f.manager.getAuthorizationState(id)).toBe('expired');
});

test('staged device removal remains visibly incomplete and does not change authoritative authorization', async () => {
    const f = await fixture(); await f.manager.initialize(); const certificate = await f.manager.createDevice(3600); const id = certificateId(certificate, context);
    await f.manager.publishDeviceState(f.descriptor.relayId, { certificates: [certificate] }); const before = f.manager.deviceState;
    f.behavior = 'staged'; await expect(f.manager.removeDevice(id)).rejects.toThrow('not yet authoritative');
    expect(f.manager.deviceState).toEqual(before); expect(f.manager.getAuthorizationState(id)).toBe('authorized'); expect((await f.pending())!.pending).toBe(false);
    f.behavior = 'accepted'; await f.manager.removeDevice(id); expect(f.manager.getAuthorizationState(id)).toBe('notRegistered');
    const count = f.requests.length; await f.manager.removeDevice(id); expect(f.requests).toHaveLength(count);
});

test('unknown device publication reuses exact bytes after restart and rejects a changed complete list', async () => {
    const first = await fixture(); await first.manager.initialize(); const local = await first.manager.createDevice(3600); first.behavior = 'lost';
    await expect(first.manager.publishDeviceState(first.descriptor.relayId, { certificates: [local] })).rejects.toThrow('lost');
    await first.manager.dispose(); await first.store.dispose();
    const resumed = await fixture(first.store.path); await resumed.manager.initialize();
    await expect(resumed.manager.publishDeviceState(resumed.descriptor.relayId, { certificates: [] })).rejects.toThrow('unknown result');
    await resumed.manager.publishDeviceState(resumed.descriptor.relayId, { certificates: [local] }); expect(resumed.requests).toEqual(first.requests);
    expect(resumed.manager.getAuthorizationState(certificateId(local, context))).toBe('authorized');
});

test('no implicit device-set reconstruction; explicit recovery may add the local device', async () => {
    const f = await fixture(); await f.manager.initialize(); const local = await f.manager.createDevice(3600);
    await expect(f.manager.publishDeviceState(f.descriptor.relayId)).rejects.toThrow('complete device state is unavailable');
    const recovered = await f.manager.publishDeviceState(f.descriptor.relayId, { recovery: true });
    expect(recovered.deviceState.certificates).toEqual([local]); expect(recovered.deviceState.revision).toBe(f.clock.wall * 1000);
});

test('invalid staged result preserves pending publication and definitive rejection clears it', async () => {
    const f = await fixture(); await f.manager.initialize(); const certificate = await f.manager.createDevice(3600); f.behavior = 'bad-staging';
    await expect(f.manager.publishDeviceState(f.descriptor.relayId, { certificates: [certificate] })).rejects.toThrow('expired'); expect((await f.pending())!.pending).toBe(true);
    f.behavior = 'reject'; await expect(f.manager.publishDeviceState(f.descriptor.relayId, { certificates: [certificate] })).rejects.toThrow('Rejected'); expect((await f.pending())!.pending).toBe(false);
});

test('device revocation refresh falls back to the account session and invalidates only device sessions', async () => {
    const f = await fixture(); await f.manager.initialize(); const local = await f.manager.createDevice(3600);
    await f.manager.publishDeviceState(f.descriptor.relayId, { certificates: [local] });
    const device = await f.pool.get(f.descriptor.relayId, { mode: 'device', signer: f.manager });
    f.state = f.signState({ ...f.state!, revision: f.state!.revision + 1, certificates: [] }); f.deviceDenied = true;
    await f.manager.getOwnDeviceState(f.descriptor.relayId); expect(f.modes).toEqual(['device', 'account']);
    expect(f.manager.getAuthorizationState(certificateId(local, context))).toBe('notRegistered'); expect(device.isDisposed).toBe(true);
    expect(f.pool.clients.some(client => client.sessionMode === 'account' && !client.isDisposed)).toBe(true);
});

test('resolved older/conflicting/forged states cannot overwrite accepted authorization', async () => {
    const f = await fixture(); await f.manager.initialize(); const local = await f.manager.createDevice(3600);
    await f.manager.publishDeviceState(f.descriptor.relayId, { certificates: [local], revision: 10 }); const accepted = f.state!;
    for (const value of [f.signState({ ...accepted, revision: 9 }), f.signState({ ...accepted, certificates: [] }), { ...accepted, revision: 11, accountSignature: new Uint8Array(64) }]) {
        f.state = value; await expect(f.manager.getOwnDeviceState(f.descriptor.relayId)).rejects.toThrow(); expect(f.manager.deviceState).toEqual(accepted);
    }
});

test('runtime coalesces notifications, reports lagging state, polls and allows stop from a state observer', async () => {
    const f = await fixture(); const time = controlDelays(f.clock); await f.manager.initialize(); const local = await f.manager.createDevice(3600);
    await f.manager.publishDeviceState(f.descriptor.relayId, { certificates: [local] }); await f.manager.start();
    await vi.waitFor(() => expect(f.sockets[0]?.authenticated).toBe(true));
    f.sockets[0]!.notify(5); f.sockets[0]!.notify(3);
    await vi.waitFor(() => expect(f.manager.lastBackgroundError?.error).toMatchObject({ code: 'stale_device_state' }));
    f.state = f.signState({ ...f.state!, revision: 5 });
    time.advance(5000); await vi.waitFor(() => expect(f.manager.deviceState?.revision).toBe(5));
    f.manager.on('deviceStateChanged', async state => { if (state.revision === 6) await f.manager.stop(); });
    f.state = f.signState({ ...f.state!, revision: 6 });
    await vi.waitFor(() => expect([...time.waits].some(wait => wait.milliseconds === 30000)).toBe(true)); time.advance(30000);
    await vi.waitFor(() => expect(f.manager.lifecycleState).toBe('stopped'));
    expect(f.manager.deviceState!.revision).toBe(6); expect(f.pool.clients.every(client => !client.isDisposed)).toBe(true);
    await f.manager.start(); expect(f.manager.lifecycleState).toBe('running'); await f.manager.stop();
});

test('background error observers can stop runtime, and observer failures retain visible diagnostics', async () => {
    const f = await fixture(); controlDelays(f.clock); await f.manager.initialize(); const local = await f.manager.createDevice(3600);
    await f.manager.publishDeviceState(f.descriptor.relayId, { certificates: [local] }); await f.manager.start();
    await vi.waitFor(() => expect(f.sockets[0]?.authenticated).toBe(true));
    f.manager.onLifecycle('backgroundError', async () => { await f.manager.stop(); throw new Error('observer failed after stop'); });
    f.sockets[0]!.notify(10);
    await vi.waitFor(() => expect(f.manager.lifecycleState).toBe('stopped'));
    await vi.waitFor(() => expect(f.manager.lastBackgroundError?.error).toBeInstanceOf(AggregateError));
});

test('contact-authorized device queries sign the complete proof and verify current inviter/grant signatures before caching', async () => {
    const f = await fixture(); await f.manager.initialize(); await f.manager.createDevice(3600);
    const fixtureData = vector<{ grants: { signing_devices: { certificate: Record<string, never>; signing_private_key: string }[] } }>('contacts').grants.signing_devices[0]!;
    const remote = deviceCertificateCodec.decode(fixtureData.certificate); const remoteKey = decodeBase64Url(fixtureData.signing_private_key);
    const accountFixture = vector<{ device_certificate: { private_key: string } }>('identity-auth').device_certificate;
    const remoteState: AccountDeviceState = { account: remote.account, accountPublicKey: remote.accountPublicKey, revision: 10, certificates: [remote], accountSignature: new Uint8Array(64) };
    f.state = { ...remoteState, accountSignature: signAccount(deviceStateInput(remoteState, context), decodeBase64Url(accountFixture.private_key)) };
    const inviteBody = { inviter: remote.account, signerDeviceId: certificateId(remote, context), expiresAt: f.clock.wall + 3600, deviceSignature: new Uint8Array(64) };
    const invite = { ...inviteBody, deviceSignature: signDevice(contactInviteInput(inviteBody, context), remoteKey) };
    expect((await f.manager.getDeviceState(invite))!.account).toBe(remote.account);
    const grantBody = { grantor: remote.account, grantee: accountId, signatures: {} };
    const grant = { ...grantBody, signatures: { [certificateId(remote, context)]: signDevice(contactGrantInput(grantBody, context), remoteKey) } };
    expect((await f.manager.getDeviceState(grant))!.revision).toBe(10);
    const current = await f.store.read([{ collection: 'device_states', key: remote.account }]);
    await expect(f.manager.getDeviceState({ ...invite, deviceSignature: new Uint8Array(64) })).rejects.toThrow('signature');
    expect((await f.store.read([{ collection: 'device_states', key: remote.account }])).sets).toEqual(current.sets);
    await expect(f.manager.getDeviceState({ ...grant, grantee: remote.account })).rejects.toThrow('authorize this account');
    const before = f.modes.length; f.deviceDenied = true;
    await expect(f.manager.getDeviceState(invite)).rejects.toMatchObject({ code: 'device_unknown' }); expect(f.modes).toHaveLength(before + 1);
});
