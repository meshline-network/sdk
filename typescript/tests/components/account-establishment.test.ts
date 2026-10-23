import { afterEach, describe, expect, test, vi } from 'vitest';
import { randomBytes } from 'node:crypto';
import {
    AccountEstablishmentError, SecretAccessError, accountDeviceStateCodec, canonicalJson, certificateId,
    deviceStateInput, deviceCertificateCodec, encodeBase64Url, parseJson, requireObject, systemRandom, contactGrantInput, contactGrantCodec, contactRecordCodec,
} from '@meshline/sdk';
import { ClientNetwork } from '../support/client-network.js';
import { context } from '../support/relay-fixture.js';

const networks: ClientNetwork[] = [];
afterEach(async () => { vi.restoreAllMocks(); for (const network of networks.splice(0)) await network.dispose(); });
const progress = { collection: 'client_account_operations', key: 'establishment' };
const preparation = { collection: 'device_preparation', key: 'current' };
const local = { collection: 'local_device', key: 'current' };
async function fixture() {
    const network = new ClientNetwork(); networks.push(network);
    return { network, ...await network.open(), relayId: network.relays[0]!.descriptor.relayId };
}
const publications = (network: ClientNetwork) => network.requests.filter(value => ['device.state.publish', 'account.route.publish', 'profile.publish'].includes(value.method));

describe('Establishment and renewal', () => {
    test('first setup initializes a profile, healthy setup requires no account signature, and fresh installations preserve other devices and profile', async () => {
        const f = await fixture();
        await f.client.establishAccount({ initialProfile: { nickname: 'Alice', bio: 'hello', publicDiscovery: true } });
        expect(f.client.lifecycleState).toBe('stopped');
        const certificate = f.client.device!; const route = f.client.route!;
        const count = publications(f.network).length; const signatures = f.network.signatures.length;
        await f.client.establishAccount({ initialProfile: { nickname: 'ignored', publicDiscovery: false } });
        expect(publications(f.network)).toHaveLength(count); expect(f.network.signatures).toHaveLength(signatures);
        const next = await f.network.open();
        await next.client.establishAccount({ initialProfile: { nickname: 'also ignored' } });
        expect(next.client.deviceState!.certificates.map(value => certificateId(value, context)))
            .toEqual([certificateId(certificate, context), certificateId(next.client.device!, context)]);
        expect(next.client.route).toEqual(route); expect(next.client.profile).toMatchObject({ nickname: 'Alice', bio: 'hello', publicDiscovery: true });
    });

    test('setup uses the supplied platform random source for durable operation IDs as well as device keys', async () => {
        const network = new ClientNetwork({ bytes: size => new Uint8Array(randomBytes(size)) }); networks.push(network);
        const f = await network.open(); vi.spyOn(systemRandom, 'bytes').mockImplementation(() => { throw new Error('No Web Crypto on this platform'); });
        await f.client.establishAccount(); expect(f.client.deviceState!.certificates).toHaveLength(1);
    });

    test('healthy setup while running reuses effective authorization despite a separate unconfirmed route renewal', async () => {
        const f = await fixture(); await f.client.establishAccount();
        f.network.failBefore = 'account.route.publish';
        await expect(f.client.accountManager.publishRoute(f.relayId, { validitySeconds: 7200 })).rejects.toThrow();
        f.network.failBefore = undefined;
        await f.client.start(); const writes = publications(f.network).length; const signs = f.network.signatures.length;
        await f.client.establishAccount();
        expect(f.client.lifecycleState).toBe('running'); expect(publications(f.network)).toHaveLength(writes); expect(f.network.signatures).toHaveLength(signs);
        expect((await f.store.read([{ collection: 'signed_requests', key: 'account.route.publish' }])).sets[0]![0]!.value.pending).toBe(true);
    });

    test('a new device does not initialize an unavailable existing profile with supplied defaults', async () => {
        const f = await fixture(); await f.client.establishAccount({ initialProfile: { nickname: 'retained' } });
        f.network.relays[0]!.profiles.delete(f.client.accountId);
        const next = await f.network.open(); const writes = publications(f.network).length;
        await next.client.establishAccount({ initialProfile: { nickname: 'must not replace' } });
        expect(next.client.profile).toBeUndefined();
        expect(publications(f.network).slice(writes).map(value => value.method)).toEqual(['device.state.publish']);
    });

    test.each(['certificate', 'route'] as const)('retained expired %s renews only that prerequisite with original keys and relay', async expired => {
        const f = await fixture();
        await f.client.establishAccount({ certificateValiditySeconds: expired === 'certificate' ? 2 : 3600, routeValiditySeconds: expired === 'route' ? 2 : 3600 });
        const certificate = f.client.device!; const route = f.client.route!;
        const count = f.network.requests.length;
        await f.dispose(); f.network.clock.wall += 3;
        const resumed = await f.network.open(91, f.path);
        await resumed.client.establishAccount();
        expect(certificateId(resumed.client.device!, context)).toBe(certificateId(certificate, context));
        expect(resumed.client.route!.relayId).toBe(route.relayId);
        const writes = f.network.requests.slice(count).filter(value => value.method.endsWith('.publish'));
        expect(writes.map(value => value.method)).toEqual([expired === 'certificate' ? 'device.state.publish' : 'account.route.publish']);
        expect(resumed.client.deviceState!.certificates).toHaveLength(1);
    });
});

describe('Authorization prerequisites', () => {
    test.each(['new', 'expired', 'revoked'])('%s device without an account signer reports the required integration before publishing', async kind => {
        const f = await fixture(); await f.client.establishAccount({ certificateValiditySeconds: 1 });
        const certificate = f.client.device; const path = f.path;
        if (kind === 'expired') f.network.clock.wall += 2;
        if (kind === 'revoked') {
            const current = f.client.deviceState!; const state = { ...current, revision: current.revision + 1, certificates: [] };
            f.network.relays[0]!.states.set(f.client.accountId, { ...state, accountSignature: await f.signer.sign(deviceStateInput(state, context)) });
        }
        await f.dispose();
        const next = await f.network.open(91, kind === 'new' ? undefined : path, false);
        const writes = publications(f.network).length;
        await expect(next.client.establishAccount()).rejects.toMatchObject({ reason: 'accountSignerRequired' });
        expect(publications(f.network)).toHaveLength(writes);
        expect(next.client.device).toEqual(kind === 'new' ? undefined : certificate);
        expect((await next.store.read([progress])).sets[0]).toEqual([]);
    });

    test.each(['unknown', 'temporarilyUnavailable', 'permanentlyLost'] as const)('%s secret access preserves all retained records and does no network I/O', async diagnosis => {
        const f = await fixture(); await f.client.establishAccount();
        const before = await f.store.read([local, preparation, progress]); const count = f.network.requests.length;
        f.network.beforeUnprotect = async () => { throw new SecretAccessError(diagnosis, 'Secret store unavailable'); };
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'localKeyAccessRequired', keyIssue: diagnosis });
        expect(await f.store.read([local, preparation, progress])).toEqual(before);
        expect(f.network.requests).toHaveLength(count);
        f.network.beforeUnprotect = undefined; await f.client.establishAccount();
        expect((await f.store.read([local])).sets[0]).toEqual(before.sets[0]);
    });

    test.each(['account.route.resolve', 'device.state.resolve'])('%s failure never recreates keys, replaces the list, or switches relay', async method => {
        const f = await fixture(); await f.client.establishAccount();
        const certificate = f.client.device; const state = f.client.deviceState; const route = f.client.route;
        const writes = publications(f.network).length;
        f.network.failBefore = method;
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'accountStateUnavailable' });
        expect(f.client.device).toEqual(certificate); expect(f.client.deviceState).toEqual(state); expect(f.client.route).toEqual(route);
        expect(publications(f.network)).toHaveLength(writes);
    });

    test('a fresh installation cannot create a device before the existing complete device state is readable', async () => {
        const f = await fixture(); await f.client.establishAccount();
        const next = await f.network.open(); f.network.failBefore = 'device.state.resolve';
        const writes = publications(f.network).length;
        await expect(next.client.establishAccount()).rejects.toMatchObject({ reason: 'accountStateUnavailable' });
        expect(next.client.device).toBeUndefined(); expect(publications(f.network)).toHaveLength(writes);
        expect((await next.store.read([local, preparation, progress])).sets).toEqual([[], [], []]);
    });

    test('an unfinished home-relay migration blocks setup without changing its route or device', async () => {
        const f = await fixture(); await f.client.establishAccount();
        f.network.failBefore = 'account.route.publish';
        await expect(f.client.changeHomeRelay(f.network.relays[1]!.descriptor.relayId)).rejects.toThrow();
        const device = f.client.device; const route = f.client.route; const writes = publications(f.network).length;
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'operationParametersConflict' });
        expect(f.client.device).toEqual(device); expect(f.client.route).toEqual(route); expect(publications(f.network)).toHaveLength(writes);
    });

    test('an expired route and an unfinished publication to another relay conflict before creating setup progress', async () => {
        const f = await fixture(); await f.client.establishAccount({ routeValiditySeconds: 1 });
        f.network.loseAfter = 'account.route.publish';
        await expect(f.client.accountManager.publishRoute(f.network.relays[1]!.descriptor.relayId, { validitySeconds: 1 })).rejects.toThrow();
        f.network.loseAfter = undefined; f.network.clock.wall += 2;
        const count = publications(f.network).length;
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'relayMismatch' });
        expect(publications(f.network)).toHaveLength(count); expect((await f.store.read([progress])).sets[0]).toEqual([]);
    });
});

describe('Device selection and contact continuity', () => {
    test('full device list permits renewal without a slot and requires explicit replacement in one publication', async () => {
        const f = await fixture(); await f.client.establishAccount({ certificateValiditySeconds: 1 });
        const first = certificateId(f.client.device!, context);
        const installations = [f];
        for (let index = 1; index < 8; index++) {
            const next = await f.network.open(); await next.client.establishAccount({ certificateValiditySeconds: index === 1 ? 1 : 3600 });
            installations.push({ ...f, ...next });
        }
        const replace = certificateId(installations[1]!.client.device!, context);
        f.network.clock.wall += 2; await f.client.establishAccount();
        expect(certificateId(f.client.device!, context)).toBe(first); expect(f.client.deviceState!.certificates).toHaveLength(8);
        const next = await f.network.open();
        const error = await next.client.establishAccount().catch(error => error);
        expect(error).toBeInstanceOf(AccountEstablishmentError);
        expect(error).toMatchObject({ reason: 'deviceSelectionRequired', deviceSelection: { deviceLimit: 8, devices: expect.any(Array) } });
        expect(error.deviceSelection.devices).toHaveLength(8); expect(error.deviceSelection.previousLocalDeviceId).toBeUndefined();
        const id = certificateId(next.client.device!, context); const count = publications(f.network).length;
        await next.client.establishAccount({ replaceDeviceId: replace });
        const changes = publications(f.network).slice(count).filter(value => value.method === 'device.state.publish');
        expect(changes).toHaveLength(1);
        const ids = next.client.deviceState!.certificates.map(value => certificateId(value, context));
        expect(ids).toHaveLength(8); expect(ids).toContain(id); expect(ids).not.toContain(replace); expect(ids).toContain(first);
    }, 45000);

    test('a new installation cannot infer contact continuity for a valid remote device from an empty contact list', async () => {
        const f = await fixture(); await f.client.establishAccount();
        const next = await f.network.open(); const writes = publications(f.network).length;
        await expect(next.client.establishAccount({ replaceDeviceId: certificateId(f.client.device!, context) })).rejects.toMatchObject({
            reason: 'contactContinuityUnverifiable', contactIssues: [{ code: 'contact_history_unavailable' }],
        });
        expect(publications(f.network)).toHaveLength(writes);
        expect(f.network.relays[0]!.states.get(f.client.accountId)!.certificates).toHaveLength(1);
    });

    test('setup never replays a pending standalone removal that would break grant continuity', async () => {
        const f = await fixture(); await f.client.establishAccount();
        const other = await f.network.open(); await other.client.establishAccount({ certificateValiditySeconds: 1 });
        const peer = await f.network.open(92); const id = certificateId(f.client.device!, context); const otherId = certificateId(other.client.device!, context);
        let grant = { grantor: f.client.accountId, grantee: peer.client.accountId, signatures: {} };
        const input = contactGrantInput(grant, context);
        grant = { ...grant, signatures: { [id]: await f.client.deviceManager.sign(input), [otherId]: await other.client.deviceManager.sign(input) } };
        const snapshot = await f.store.read([]);
        await f.store.commit(snapshot.version, [{ kind: 'put', collection: 'contacts', key: peer.client.accountId, value: {
            record: contactRecordCodec.encode({ account: peer.client.accountId, status: 'active', updatedAt: f.network.clock.wall, grantToContact: grant }),
            confirmedGrantTo: contactGrantCodec.encode(grant),
        } }]);
        f.network.failBefore = 'device.state.publish';
        await expect(f.client.deviceManager.removeDevice(id)).rejects.toThrow();
        const key = { collection: 'signed_requests', key: 'device.state.publish' };
        const pending = (await f.store.read([key])).sets[0]![0]!.value;
        f.network.failBefore = undefined; f.network.clock.wall += 2;
        const writes = publications(f.network).length;
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'operationParametersConflict' });
        expect(publications(f.network)).toHaveLength(writes);
        expect((await f.store.read([key])).sets[0]![0]!.value).toEqual(pending);
        expect(f.network.relays[0]!.states.get(f.client.accountId)!.certificates.some(value => certificateId(value, context) === id)).toBe(true);
        await expect(f.client.deviceManager.removeDevice(id)).rejects.toMatchObject({ code: 'contact_grant_at_risk' });
    });

    test('replacement choice remains frozen after acceptance even when the next checkpoint failed', async () => {
        const f = await fixture(); await f.client.establishAccount({ certificateValiditySeconds: 1 });
        const other = await f.network.open(); await other.client.establishAccount({ certificateValiditySeconds: 1 });
        f.network.clock.wall += 2;
        const next = await f.network.open(); const commit = next.store.commit.bind(next.store);
        let fail = true;
        vi.spyOn(next.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            if (fail && mutations.some(value => value.kind === 'put' && value.collection === progress.collection && value.value.step === 'initializingProfile')) {
                fail = false; throw new Error('Checkpoint failed after replacement acceptance');
            }
            return commit(version, mutations, signal);
        });
        const removed = certificateId(f.client.device!, context);
        await expect(next.client.establishAccount({ replaceDeviceId: removed })).rejects.toThrow('Checkpoint failed');
        const count = publications(f.network).length;
        await expect(next.client.establishAccount({ replaceDeviceId: certificateId(other.client.device!, context) }))
            .rejects.toMatchObject({ reason: 'operationParametersConflict' });
        expect(publications(f.network)).toHaveLength(count);
        await next.client.establishAccount();
        expect(next.client.deviceState!.certificates.map(value => certificateId(value, context)))
            .toEqual([certificateId(other.client.device!, context), certificateId(next.client.device!, context)]);
    });

    test('route renewal does not reopen an accepted replacement choice after a profile interruption', async () => {
        const f = await fixture(); await f.client.establishAccount({ certificateValiditySeconds: 1, routeValiditySeconds: 5 });
        const other = await f.network.open(); await other.client.establishAccount({ certificateValiditySeconds: 1 });
        f.network.clock.wall += 2;
        const next = await f.network.open(); const removed = certificateId(f.client.device!, context);
        f.network.failBefore = 'profile.resolve';
        await expect(next.client.establishAccount({ replaceDeviceId: removed })).rejects.toMatchObject({ reason: 'accountStateUnavailable' });
        const enabledId = certificateId(next.client.device!, context); await next.dispose();
        f.network.failBefore = undefined; f.network.clock.wall += 4;
        const resumed = await f.network.open(91, next.path);
        const writes = f.network.requests.filter(value => value.method === 'device.state.publish').length;
        await expect(resumed.client.establishAccount({ replaceDeviceId: certificateId(other.client.device!, context) }))
            .rejects.toMatchObject({ reason: 'operationParametersConflict' });
        expect((await resumed.store.read([progress])).sets[0]![0]!.value.replaceDeviceId).toBe(removed);
        await resumed.client.establishAccount();
        expect(f.network.requests.filter(value => value.method === 'device.state.publish')).toHaveLength(writes);
        expect(resumed.client.deviceState!.certificates.map(value => certificateId(value, context)))
            .toEqual([certificateId(other.client.device!, context), enabledId]);
    });
});

describe('Interrupted operations and publication conflicts', () => {
    test('protected draft exists before wallet signing and a declined signature resumes the identical device after restart', async () => {
        const f = await fixture(); let draft: unknown;
        f.network.beforeSign = async input => {
            const document = requireObject(parseJson(input));
            if (document.signing_public_key === undefined) return;
            draft = (await f.store.read([preparation])).sets[0]![0]!.value;
            throw new DOMException('Wallet declined', 'NotAllowedError');
        };
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'signatureDeclined' });
        expect(draft).toMatchObject({ protectedSigningKey: expect.any(String), protectedEncryptionKey: expect.any(String) });
        const pending = (await f.store.read([preparation])).sets[0]![0]!.value;
        const id = certificateId(deviceCertificateCodec.decode(pending.certificate!), context);
        expect(f.client.device).toBeUndefined();
        await f.dispose(); f.network.beforeSign = undefined;
        const resumed = await f.network.open(91, f.path);
        await resumed.client.establishAccount();
        expect(certificateId(resumed.client.device!, context)).toBe(id);
        expect((await resumed.store.read([preparation])).sets[0]![0]!.value).toEqual({ activatedOperationId: pending.operationId });
    });

    test('initial avatar bytes survive a declined signature and caller mutation before restart', async () => {
        const f = await fixture(); const key = new Uint8Array(32).fill(7);
        const avatar = { uri: 'https://example.com/avatar.png', hash: `sha256:${encodeBase64Url(new Uint8Array(32))}`, contentType: 'image/png', size: 16,
            encryption: { alg: 'AES-256-GCM', key, nonce: new Uint8Array(12) } };
        f.network.beforeSign = async () => { throw new DOMException('Declined', 'NotAllowedError'); };
        const setup = f.client.establishAccount({ initialProfile: { avatar } }); key.fill(9);
        await expect(setup).rejects.toMatchObject({ reason: 'signatureDeclined' });
        await f.dispose(); f.network.beforeSign = undefined;
        const resumed = await f.network.open(91, f.path); await resumed.client.establishAccount();
        expect(resumed.client.profile!.avatar!.encryption!.key).toEqual(new Uint8Array(32).fill(7));
    });

    test('healthy local state does not skip an unconfirmed standalone device publication', async () => {
        const f = await fixture(); await f.client.establishAccount();
        const method = 'device.state.publish';
        f.network.failBefore = method;
        await expect(f.client.deviceManager.publishDeviceState(f.relayId)).rejects.toThrow();
        const request = { collection: 'signed_requests', key: method };
        const pending = (await f.store.read([request])).sets[0]![0]!.value;
        expect(pending.pending).toBe(true); f.network.failBefore = undefined;
        await f.client.establishAccount();
        expect((await f.store.read([request])).sets[0]![0]!.value.pending).toBe(false);
        const writes = f.network.requests.filter(value => value.method === method);
        expect(canonicalJson(writes.at(-1)!.body)).toBe(canonicalJson(pending.document!));
    });

    test('an accepted device addition with a lost acknowledgement resumes without another account signature', async () => {
        const f = await fixture(); await f.client.establishAccount();
        const next = await f.network.open(); f.network.loseAfter = 'device.state.publish';
        await expect(next.client.establishAccount()).rejects.toMatchObject({ reason: 'submissionOutcomeUnknown' });
        const id = certificateId(next.client.device!, context); await next.dispose(); f.network.loseAfter = undefined;
        const resumed = await f.network.open(91, next.path, false); const writes = publications(f.network).length;
        await resumed.client.establishAccount();
        expect(certificateId(resumed.client.device!, context)).toBe(id);
        expect(publications(f.network)).toHaveLength(writes);
        expect((await resumed.store.read([progress])).sets[0]).toEqual([]);
    });

    test.each(['device.state.publish', 'account.route.publish', 'profile.publish'])('restart reconciles an accepted %s response loss without duplicate keys or requests', async method => {
        const f = await fixture(); f.network.loseAfter = method;
        await expect(f.client.establishAccount({ initialProfile: { nickname: 'retained' } })).rejects.toMatchObject({ reason: 'submissionOutcomeUnknown' });
        const id = certificateId(f.client.device!, context); const requests = f.network.requests.filter(value => value.method === method);
        const exact = canonicalJson(requests[0]!.body);
        await f.dispose(); f.network.loseAfter = undefined;
        const resumed = await f.network.open(91, f.path); await resumed.client.establishAccount();
        expect(certificateId(resumed.client.device!, context)).toBe(id); expect(resumed.client.deviceState!.certificates).toHaveLength(1);
        expect(resumed.client.profile!.nickname).toBe('retained');
        expect(f.network.requests.filter(value => value.method === method).every(value => canonicalJson(value.body) === exact)).toBe(true);
        expect((await resumed.store.read([progress])).sets[0]).toEqual([]);
    });

    test.each(['ensuringRoute', 'initializingProfile'])('a failed %s checkpoint reuses activated keys and reconciles publication on restart', async step => {
        const f = await fixture(); const commit = f.store.commit.bind(f.store); let fail = true;
        vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            if (fail && mutations.some(value => value.kind === 'put' && value.collection === progress.collection && value.value.step === step)) {
                fail = false; throw new Error('Injected checkpoint failure');
            }
            return commit(version, mutations, signal);
        });
        await expect(f.client.establishAccount()).rejects.toThrow('Injected checkpoint failure');
        const id = certificateId(f.client.device!, context); const signs = f.network.signatures.filter(value => value.signing_public_key !== undefined).length;
        await f.dispose(); const resumed = await f.network.open(91, f.path); await resumed.client.establishAccount();
        expect(certificateId(resumed.client.device!, context)).toBe(id);
        expect(f.network.signatures.filter(value => value.signing_public_key !== undefined)).toHaveLength(signs);
        expect(resumed.client.deviceState!.certificates).toHaveLength(1);
    });

    test('completion expiry restores the same progress record and renews only the expired prerequisite', async () => {
        const f = await fixture(); const commit = f.store.commit.bind(f.store); let expire = true; let operationId: unknown;
        vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            const completing = expire && mutations.some(value => value.kind === 'delete' && value.collection === progress.collection);
            if (completing) operationId = (await f.store.read([progress])).sets[0]![0]!.value.operationId;
            const result = await commit(version, mutations, signal);
            if (completing) { expire = false; f.network.clock.wall += 2; }
            return result;
        });
        await expect(f.client.establishAccount({ routeValiditySeconds: 1 })).rejects.toMatchObject({ reason: 'authorizationNotYetEffective' });
        expect((await f.store.read([progress])).sets[0]![0]!.value.operationId).toBe(operationId);
        const id = certificateId(f.client.device!, context); const writes = publications(f.network).length;
        await f.client.establishAccount();
        expect(certificateId(f.client.device!, context)).toBe(id);
        expect(publications(f.network).slice(writes).map(value => value.method)).toEqual(['account.route.publish']);
    });

    test('an expired pending certificate is replayed exactly before renewing its original keys', async () => {
        const f = await fixture(); await f.client.establishAccount({ certificateValiditySeconds: 1 });
        const id = certificateId(f.client.device!, context);
        f.network.failBefore = 'device.state.publish';
        await expect(f.client.deviceManager.publishDeviceState(f.relayId)).rejects.toThrow();
        const key = { collection: 'signed_requests', key: 'device.state.publish' };
        const pending = (await f.store.read([key])).sets[0]![0]!.value;
        f.network.failBefore = undefined; f.network.clock.wall += 2;
        const before = f.network.requests.length; await f.client.establishAccount();
        const writes = f.network.requests.slice(before).filter(value => value.method === 'device.state.publish');
        expect(writes).toHaveLength(2); expect(canonicalJson(writes[0]!.body)).toBe(canonicalJson(pending.document!));
        expect(certificateId(f.client.device!, context)).toBe(id); expect(f.client.device!.expiresAt).toBeGreaterThan(f.network.clock.wall);
    });

    test('a rejected revision followed by an unknown submission retains the higher signed revision on restart', async () => {
        const f = await fixture(); f.network.rejectBefore = { method: 'device.state.publish', code: 'forbidden' };
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'accountStateUnavailable' });
        const id = certificateId(f.client.device!, context);
        f.network.rejectBefore = undefined; f.network.failBefore = 'device.state.publish';
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'submissionOutcomeUnknown' });
        const key = { collection: 'signed_requests', key: 'device.state.publish' };
        const pending = (await f.store.read([key])).sets[0]![0]!.value; expect(pending.revision).toBe(1);
        await f.dispose(); f.network.failBefore = undefined;
        const resumed = await f.network.open(91, f.path); const before = f.network.requests.length;
        await resumed.client.establishAccount();
        const writes = f.network.requests.slice(before).filter(value => value.method === 'device.state.publish');
        expect(writes).toHaveLength(1); expect(canonicalJson(writes[0]!.body)).toBe(canonicalJson(pending.document!));
        expect(certificateId(resumed.client.device!, context)).toBe(id);
    });

    test('a relay revision conflict rereads complete state before applying only the local add', async () => {
        const f = await fixture(); await f.client.establishAccount();
        const next = await f.network.open(); const original = f.client.deviceState!;
        f.network.beforeDevice = async proposed => {
            f.network.beforeDevice = undefined;
            const updated = { ...original, revision: proposed.revision + 1 };
            const signed = { ...updated, accountSignature: await f.signer.sign(deviceStateInput(updated, context)) };
            f.network.relays[0]!.states.set(f.client.accountId, signed);
        };
        await next.client.establishAccount();
        expect(next.client.deviceState!.revision).toBe(original.revision + 3);
        expect(next.client.deviceState!.certificates).toHaveLength(2);
        expect(accountDeviceStateCodec.stringify(f.network.relays[0]!.states.get(f.client.accountId)!)).toBe(accountDeviceStateCodec.stringify(next.client.deviceState!));
    });

    test('a pending replay handles relay conflicts by rereading and merging the current device list', async () => {
        const f = await fixture(); await f.client.establishAccount();
        const another = await f.network.open(); const extra = await another.client.deviceManager.createDevice(3600);
        const next = await f.network.open();
        f.network.failBefore = 'device.state.publish';
        await expect(next.client.establishAccount()).rejects.toMatchObject({ reason: 'submissionOutcomeUnknown' });
        f.network.failBefore = undefined;
        f.network.beforeDevice = async proposed => {
            f.network.beforeDevice = undefined;
            const state = { ...proposed, certificates: [f.client.device!, extra] };
            f.network.relays[0]!.states.set(f.client.accountId, { ...state, accountSignature: await f.signer.sign(deviceStateInput(state, context)) });
        };
        await next.client.establishAccount();
        expect(next.client.deviceState!.certificates.map(value => certificateId(value, context))).toEqual([certificateId(f.client.device!, context), certificateId(extra, context), certificateId(next.client.device!, context)]);
        expect(next.client.deviceState!.revision).toBe(2);
    });
});

describe('Startup and authorization loss', () => {
    test('a valid restored installation starts while offline without wallet signing or waiting for discovery', async () => {
        const f = await fixture(); await f.client.establishAccount(); await f.dispose();
        const resumed = await f.network.open(91, f.path, false); f.network.unavailableRelay = f.relayId;
        const signs = f.network.signatures.length;
        await resumed.client.start(); expect(resumed.client.lifecycleState).toBe('running');
        expect(f.network.signatures).toHaveLength(signs);
        await resumed.client.stop(); expect(resumed.pool.clients.filter(value => value.sessionMode === 'device')).toEqual([]);
    });

    test('canceling startup queued behind wallet authorization does not cancel or drain setup', async () => {
        const f = await fixture(); let release!: () => void; let entered!: () => void;
        const ready = new Promise<void>(resolve => { entered = resolve; });
        const wait = new Promise<void>(resolve => { release = resolve; });
        f.network.beforeSign = async input => { if (requireObject(parseJson(input)).signing_public_key !== undefined) { entered(); await wait; } };
        const setup = f.client.establishAccount(); await ready;
        const controller = new AbortController(); const start = f.client.start(controller.signal);
        const canceled = expect(start).rejects.toMatchObject({ name: 'AbortError' });
        try { controller.abort(); await canceled; }
        finally { release(); }
        await setup; expect(f.client.deviceState!.certificates).toHaveLength(1);
        await f.client.start(); expect(f.client.lifecycleState).toBe('running');
    });

    test('revocation stops background work and requires explicit setup to reauthorize the same keys', async () => {
        const f = await fixture(); await f.client.establishAccount(); await f.client.start();
        const id = certificateId(f.client.device!, context); const original = f.client.deviceState!;
        const unsigned = { ...original, certificates: [], revision: original.revision + 1 };
        const revoked = { ...unsigned, accountSignature: await f.signer.sign(deviceStateInput(unsigned, context)) };
        f.network.relays[0]!.states.set(f.client.accountId, revoked);
        const signatures = f.network.signatures.length;
        await f.client.deviceManager.refreshDeviceState();
        await vi.waitFor(() => expect(f.client.lifecycleState).toBe('stopped'));
        const requests = f.network.requests.length; for (let index = 0; index < 4; index++) f.network.clock.tick();
        await Promise.resolve(); expect(f.network.requests).toHaveLength(requests); expect(f.network.signatures).toHaveLength(signatures);
        await f.client.establishAccount(); expect(certificateId(f.client.device!, context)).toBe(id);
        await f.client.start(); expect(f.client.lifecycleState).toBe('running');
    });

    test('an authentication rejection during startup drains children and stops further device connections', async () => {
        const f = await fixture(); await f.client.establishAccount(); await f.dispose();
        const state = f.network.relays[0]!.states.get(f.client.accountId)!;
        const unsigned = { ...state, certificates: [], revision: state.revision + 1 };
        f.network.relays[0]!.states.set(f.client.accountId, { ...unsigned, accountSignature: await f.signer.sign(deviceStateInput(unsigned, context)) });
        const resumed = await f.network.open(91, f.path); const signs = f.network.signatures.length;
        await resumed.client.start().catch(() => undefined);
        await vi.waitFor(() => expect(resumed.client.lifecycleState).toBe('stopped'));
        const requests = f.network.requests.length; for (let index = 0; index < 4; index++) f.network.clock.tick();
        await new Promise(resolve => setTimeout(resolve, 0));
        expect(f.network.requests).toHaveLength(requests); expect(f.network.signatures).toHaveLength(signs);
        expect(resumed.pool.clients.filter(value => value.sessionMode === 'device')).toEqual([]);
    });

    test('own-state authorization rejection stops background work without treating a temporary failure as revocation', async () => {
        const f = await fixture(); await f.client.establishAccount(); await f.client.start();
        f.network.failBefore = 'device.state.resolve';
        await expect(f.client.deviceManager.refreshDeviceState()).rejects.toThrow();
        expect(f.client.lifecycleState).toBe('running'); f.network.failBefore = undefined;
        f.network.clock.elapsed += 61000; f.network.rejectBefore = { method: 'device.state.resolve', code: 'unauthorized' };
        await expect(f.client.deviceManager.refreshDeviceState()).rejects.toThrow();
        await vi.waitFor(() => expect(f.client.lifecycleState).toBe('stopped'));
        const requests = f.network.requests.length; const signs = f.network.signatures.length;
        f.network.clock.tick(); await new Promise(resolve => setTimeout(resolve, 0));
        expect(f.network.requests).toHaveLength(requests); expect(f.network.signatures).toHaveLength(signs);
    });
});
