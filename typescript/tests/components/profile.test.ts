import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test } from 'vitest';
import {
    AccountManager, ProfileManager, RelayClientPool, accountProfileCodec, accountRouteCodec, certificateAccountInput, certificateDeviceInput,
    devicePublicKey, encryptionPublicKey, accountPublicKey, getAccountId, profileInput, profileResolveResultCodec, relayDescriptorCodec,
    routeAccountInput, routeRelayInput, signAccount, signDevice, type AccountProfile, type AccountRoute, type DeviceCertificate,
    type DeviceSigner, type ProfileResolveResult, type RelayFetch, type JsonObject,
} from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { context, relayPrivateKey, signedDescriptor, TestRegistry } from '../support/relay-fixture.js';
import { AdvancingClock } from '../support/clock.js';
import { removeTestDirectory } from '../support/temp.js';

const accountKey = new Uint8Array(32).fill(3); const deviceKey = new Uint8Array(32).fill(4); const accountId = getAccountId('neo:860833102', accountPublicKey(accountKey));
const resources: { dispose(): Promise<void> }[] = []; const directories: string[] = [];
afterEach(async () => { for (const resource of resources.splice(0).reverse()) await resource.dispose(); for (const path of directories.splice(0)) await removeTestDirectory(path); });
function certificate(): DeviceCertificate {
    let value: DeviceCertificate = { account: accountId, accountPublicKey: accountPublicKey(accountKey), signingPublicKey: devicePublicKey(deviceKey),
        encryptionPublicKey: encryptionPublicKey(deviceKey), notBefore: 1729999900, expiresAt: 1731000000, deviceSignature: new Uint8Array(64), accountSignature: new Uint8Array(64) };
    value = { ...value, deviceSignature: signDevice(certificateDeviceInput(value, context), deviceKey) };
    return { ...value, accountSignature: signAccount(certificateAccountInput(value, context), accountKey) };
}
function response(body: unknown, status = 200): Response { return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }); }
async function fixture(path?: string) {
    if (!path) { const directory = await mkdtemp(join(tmpdir(), 'meshline-profile-')); directories.push(directory); path = join(directory, 'state.sqlite'); }
    const store = new NodeSqliteStore(path); resources.push(store); await store.migrate();
    const clock = new AdvancingClock(); const descriptor = signedDescriptor(); const cert = certificate(); const requests: string[] = [];
    let result: ProfileResolveResult | undefined; let loseResponse = false; let reject = false; let profileSigns = 0;
    let route: AccountRoute = { account: accountId, accountPublicKey: cert.accountPublicKey, revision: 0, relayId: descriptor.relayId,
        updatedAt: clock.wall, expiresAt: clock.wall + 315360000, accountSignature: new Uint8Array(64) };
    route = { ...route, accountSignature: signAccount(routeAccountInput(route, context), accountKey) };
    route = { ...route, relaySignature: signAccount(routeRelayInput(route, context), relayPrivateKey) };
    const signer: DeviceSigner = { certificate: cert, async sign(input) { profileSigns++; return signDevice(input, deviceKey); } };
    const fetch: RelayFetch = async (url, init) => {
        const name = new URL(url).pathname.replace(/^\/v1/, '').slice(1).replaceAll('/', '.');
        if (name === 'relay.descriptor') return response(relayDescriptorCodec.encode(descriptor));
        if (name === 'auth.challenge') return response({ nonce: 'challenge', created_at: clock.wall, expires_at: clock.wall + 300 });
        if (name === 'auth.device.verify') return response({ token: 'device-session', mode: 'device', expires_at: clock.wall + 3600 });
        if (name === 'account.route.resolve') return response(accountRouteCodec.encode(route));
        if (name === 'profile.resolve') return result ? response(profileResolveResultCodec.encode(result)) : response({ code: 'not_found', message: 'Unknown profile' }, 404);
        if (name !== 'profile.publish') throw new Error(`Unexpected method ${name}`);
        expect(init.headers['X-Meshline-Session']).toBe('device-session'); requests.push(init.body!);
        expect((await store.read([{ collection: 'signed_requests', key: name }])).sets[0]![0]!.value.pending).toBe(true);
        if (reject) return response({ code: 'forbidden', message: 'Rejected' }, 403);
        result = { profile: accountProfileCodec.parse(init.body!), signerCertificate: cert };
        if (loseResponse) throw new TypeError('Profile accepted but response was lost');
        return new Response(null, { status: 204 });
    };
    const pool = new RelayClientPool({ context, accountId, fetch, clock }, new TestRegistry()); resources.push(pool);
    const account = new AccountManager({ context, accountId, store, relayClients: pool, clock }); resources.push(account); await account.initialize();
    const manager = new ProfileManager({ context, accountId, store, relayClients: pool, accountManager: account, deviceSigner: signer, clock }); resources.push(manager);
    await manager.initialize();
    function signedProfile(overrides: Partial<AccountProfile> = {}): ProfileResolveResult {
        const profile: AccountProfile = { account: accountId, publicDiscovery: false, updatedAt: clock.wall, deviceSignature: new Uint8Array(64), ...overrides };
        return { profile: { ...profile, deviceSignature: signDevice(profileInput(profile, context), deviceKey) }, signerCertificate: cert };
    }
    return { store, manager, account, pool, clock, cert, requests, signedProfile,
        set result(value: ProfileResolveResult | undefined) { result = value; }, get result() { return result; },
        set loseResponse(value: boolean) { loseResponse = value; }, set reject(value: boolean) { reject = value; }, get signs() { return profileSigns; },
        async pending() { return (await store.read([{ collection: 'signed_requests', key: 'profile.publish' }])).sets[0]![0]?.value; },
    };
}
test('profile updates preserve omitted and unknown fields, delete with null, and allow same-second acceptance', async () => {
    const f = await fixture(); f.result = f.signedProfile({ nickname: 'Alice', bio: 'bio', additionalProperties: { future: { keep: true } } });
    const first = await f.manager.updateProfile({ nickname: '爱丽丝', publicDiscovery: true });
    expect(first).toMatchObject({ nickname: '爱丽丝', bio: 'bio', publicDiscovery: true, additionalProperties: { future: { keep: true } } });
    const second = await f.manager.updateProfile({ nickname: null, bio: '' }); expect(second.nickname).toBeUndefined(); expect(second.bio).toBe(''); expect(second.updatedAt).toBe(first.updatedAt);
    expect((await f.pending())!.pending).toBe(false);
    const saved = await f.store.read([{ collection: 'profiles', key: accountId }, { collection: 'signed_requests', key: 'profile.publish' }]);
    expect(saved.sets[0]![0]!.revision).toBe(saved.sets[1]![0]!.revision);
    second.deviceSignature.fill(0); expect(f.manager.profile!.deviceSignature).not.toEqual(second.deviceSignature);
});
test('lost profile acceptance retries exact bytes after restart and rejects conflicting updates', async () => {
    const first = await fixture(); first.loseResponse = true;
    await expect(first.manager.updateProfile({ nickname: 'Alice' })).rejects.toThrow('lost'); expect((await first.pending())!.pending).toBe(true);
    await first.manager.dispose(); await first.account.dispose(); await first.pool.dispose(); await first.store.dispose();
    const resumed = await fixture(first.store.path); resumed.clock.wall += 60;
    await expect(resumed.manager.updateProfile({ nickname: 'Bob' })).rejects.toThrow('unknown result');
    await resumed.manager.updateProfile({ nickname: 'Alice' }); expect(resumed.requests).toEqual(first.requests);
    expect((await resumed.pending())!.pending).toBe(false);
});
test('resolved latest profile clears a matching pending write, while a same-time different profile does not', async () => {
    const f = await fixture(); f.loseResponse = true;
    await expect(f.manager.updateProfile({ nickname: 'Alice' })).rejects.toThrow(); const accepted = f.result;
    f.result = f.signedProfile({ nickname: 'different' }); await f.manager.getProfile(); expect((await f.pending())!.pending).toBe(true);
    f.result = accepted; await f.manager.getProfile(); expect((await f.pending())!.pending).toBe(false);
});
test('current profile remains readable with a historically valid certificate after expiry', async () => {
    const f = await fixture(); f.result = f.signedProfile({ nickname: 'historic', updatedAt: 1730000000 });
    // Keep the caller session valid while advancing only the profile validation time.
    await f.pool.get(signedDescriptor().relayId, { mode: 'device', signer: { certificate: f.cert, sign: async input => signDevice(input, deviceKey) } });
    f.clock.wall = f.cert.expiresAt + 100;
    expect((await f.manager.getProfile())!.nickname).toBe('historic');
});
test('profile signature and certificate account signatures are both required; older reads preserve cached profile', async () => {
    const f = await fixture(); f.result = f.signedProfile({ nickname: 'known', updatedAt: f.clock.wall + 100 }); await f.manager.getProfile();
    const known = f.manager.profile;
    const valid = f.signedProfile({ updatedAt: f.clock.wall + 101 });
    for (const value of [f.signedProfile({ updatedAt: f.clock.wall }), { ...valid, profile: { ...valid.profile, deviceSignature: new Uint8Array(64) } },
        { ...valid, signerCertificate: { ...valid.signerCertificate, accountSignature: new Uint8Array(64) } }]) {
        f.result = value; await expect(f.manager.getProfile()).rejects.toThrow(); expect(f.manager.profile).toEqual(known);
    }
});
test('definitive rejection permits a new update and not_found clears only the in-memory current profile', async () => {
    const f = await fixture(); f.reject = true;
    await expect(f.manager.updateProfile({ bio: 'rejected' })).rejects.toMatchObject({ code: 'forbidden' }); expect((await f.pending())!.pending).toBe(false);
    f.reject = false; await f.manager.updateProfile({ bio: 'accepted' }); f.result = undefined;
    expect(await f.manager.getProfile()).toBeUndefined(); expect(f.manager.profile).toBeUndefined();
    expect((await f.manager.publishProfile())!.bio).toBe('accepted');
});
test('invalid updates fail before network publication and cannot delete publicDiscovery', async () => {
    const f = await fixture();
    await expect(f.manager.updateProfile({ publicDiscovery: null } as unknown as { publicDiscovery: boolean })).rejects.toThrow();
    await expect(f.manager.updateProfile({ nickname: '\u0085' })).rejects.toThrow();
    expect(f.requests).toEqual([]); expect(await f.pending()).toBeUndefined();
});
