import { afterEach, describe, expect, test, vi } from 'vitest';
import { URL as ExpoUrl } from 'whatwg-url-minimum';
import {
    FieldUpdate, canonicalJson, certificateId, deviceCertificateCodec, parseJson, requireObject,
    type DeviceRenewalOptions,
} from '@meshline/sdk';
import { ClientNetwork } from '../support/client-network.js';
import { context } from '../support/relay-fixture.js';

const gateway = 'https://PUSH.example.com:443/api/push/notifications?app=meshline&route=chat';
const nextGateway = 'https://push-next.example.com/callback?';
const local = { collection: 'local_device', key: 'current' };
const preparation = { collection: 'device_preparation', key: 'current' };
const progress = { collection: 'client_account_operations', key: 'establishment' };
const request = { collection: 'signed_requests', key: 'device.state.publish' };
const networks: ClientNetwork[] = [];
afterEach(async () => { vi.unstubAllGlobals(); vi.restoreAllMocks(); for (const network of networks.splice(0)) await network.dispose(); });
async function fixture() {
    const network = new ClientNetwork(); networks.push(network);
    return { network, ...await network.open(), relayId: network.relays[0]!.descriptor.relayId };
}

describe('creation and renewal', () => {
    test.each(['create', 'establish'].flatMap(operation =>
        [gateway, 'https://cafe\u0301.example/callback'].map(pushGateway => ({ operation, pushGateway })),
    ))('$operation signs and persists original $pushGateway, retained by numeric renewal', async ({ operation, pushGateway }) => {
        const f = await fixture();
        if (operation === 'create') await f.client.deviceManager.createDevice({ validitySeconds: 86400, pushGateway });
        else await f.client.establishAccount({ pushGateway });
        const id = certificateId(f.client.device!, context);
        expect(f.client.device!.pushGateway).toBe(pushGateway);
        await f.dispose();
        const resumed = await f.network.open(91, f.path);
        expect(resumed.client.device!.pushGateway).toBe(pushGateway);
        const renewed = await resumed.client.deviceManager.renewDevice(7 * 86400);
        expect(renewed.pushGateway).toBe(pushGateway);
        expect(certificateId(renewed, context)).toBe(id);
        await resumed.client.establishAccount();
        expect(resumed.client.deviceState!.certificates[0]!.pushGateway).toBe(pushGateway);
    });

    test('gateway add, change and deletion preserve keys and other devices, replace sessions and publish exact certificates', async () => {
        const f = await fixture(); await f.client.establishAccount();
        const other = await f.network.open(); await other.client.establishAccount();
        const otherCertificate = other.client.device!;
        const original = (await f.store.read([local])).sets[0]![0]!.value;
        const id = certificateId(f.client.device!, context);
        for (const pushGateway of [gateway, nextGateway, FieldUpdate.delete] as const) {
            const session = await f.pool.get(f.relayId, { mode: 'device', signer: f.client.deviceManager });
            const renewed = await f.client.deviceManager.renewDevice({ pushGateway });
            expect(renewed.pushGateway).toBe(pushGateway === FieldUpdate.delete ? undefined : pushGateway);
            expect(certificateId(renewed, context)).toBe(id);
            expect(session.isDisposed).toBe(true);
            const stored = (await f.store.read([local])).sets[0]![0]!.value;
            expect(stored.protectedSigningKey).toBe(original.protectedSigningKey);
            expect(stored.protectedEncryptionKey).toBe(original.protectedEncryptionKey);
            await f.client.establishAccount();
            expect(f.client.deviceState!.certificates).toContainEqual(renewed);
            expect(f.client.deviceState!.certificates).toContainEqual(otherCertificate);
            const proof = f.network.requests.filter(value => value.method === 'auth.device.verify').at(-1)!.body;
            expect(proof.signer_certificate).toEqual(deviceCertificateCodec.encode(renewed));
        }
        expect(deviceCertificateCodec.encode(f.client.device!)).not.toHaveProperty('push_gateway');
        await f.dispose();
        const resumed = await f.network.open(91, f.path);
        expect(resumed.client.device!.pushGateway).toBeUndefined();
    });

    test.each([undefined, gateway])('omitted or unspecified renewal retains %s', async pushGateway => {
        const f = await fixture(); await f.client.establishAccount(pushGateway === undefined ? {} : { pushGateway });
        for (const options of [{}, { pushGateway: FieldUpdate.unspecified }] satisfies DeviceRenewalOptions[]) {
            const renewed = await f.client.deviceManager.renewDevice(options);
            expect(renewed.pushGateway).toBe(pushGateway);
            expect(renewed.expiresAt - renewed.notBefore).toBe(365 * 86400);
        }
    });

    test('failed local renewal does not replace the certificate or retire sessions', async () => {
        const f = await fixture(); await f.client.establishAccount({ pushGateway: gateway });
        const previous = f.client.device;
        const session = await f.pool.get(f.relayId, { mode: 'device', signer: f.client.deviceManager });
        vi.spyOn(f.store, 'commit').mockRejectedValueOnce(new Error('Commit failed'));
        await expect(f.client.deviceManager.renewDevice({ pushGateway: nextGateway })).rejects.toThrow('Commit failed');
        expect(f.client.device).toEqual(previous); expect(session.isDisposed).toBe(false);
        expect((await f.store.read([local])).sets[0]![0]!.value.certificate).toEqual(deviceCertificateCodec.encode(previous!));
    });
});

describe('account establishment and recovery', () => {
    test.each([false, true])('expired automatic renewal preserves the current declaration (removed=%s)', async remove => {
        const f = await fixture(); await f.client.establishAccount({ pushGateway: gateway });
        await f.client.deviceManager.renewDevice({ validitySeconds: 1, pushGateway: remove ? FieldUpdate.delete : nextGateway });
        await f.client.establishAccount(); f.network.clock.wall += 2; await f.dispose();
        const resumed = await f.network.open(91, f.path);
        await resumed.client.establishAccount({ pushGateway: gateway });
        expect(resumed.client.device!.pushGateway).toBe(remove ? undefined : nextGateway);
        expect(resumed.client.deviceState!.certificates).toContainEqual(resumed.client.device);
    });

    test.each([undefined, gateway])('creation intent %s survives failure before the draft exists and resumes after restart', async pushGateway => {
        const f = await fixture(); const commit = f.store.commit.bind(f.store);
        vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            if (mutations.some(value => value.kind === 'put' && value.collection === preparation.collection)) throw new Error('Draft storage unavailable');
            return commit(version, mutations, signal);
        });
        await expect(f.client.establishAccount(pushGateway === undefined ? {} : { pushGateway })).rejects.toThrow('Draft storage unavailable');
        const snapshot = await f.store.read([progress, preparation, local]);
        expect(snapshot.sets[0]![0]!.value.initialPushGateway).toBe(pushGateway);
        expect(snapshot.sets.slice(1)).toEqual([[], []]);
        await f.dispose();
        const resumed = await f.network.open(91, f.path);
        await expect(resumed.client.establishAccount({ pushGateway: nextGateway })).rejects.toMatchObject({ reason: 'operationParametersConflict' });
        expect((await resumed.store.read([progress])).sets[0]).toEqual(snapshot.sets[0]);
        await resumed.client.establishAccount();
        expect(resumed.client.device!.pushGateway).toBe(pushGateway);
        expect((await resumed.store.read([progress])).sets[0]).toEqual([]);
    });

    test.each([undefined, gateway])('expired draft preserves keys and gateway %s despite new creation parameters', async pushGateway => {
        const f = await fixture();
        f.network.beforeSign = async input => {
            if (requireObject(parseJson(input)).signing_public_key !== undefined) throw new DOMException('Declined', 'NotAllowedError');
        };
        await expect(f.client.establishAccount({ certificateValiditySeconds: 1, ...(pushGateway === undefined ? {} : { pushGateway }) })).rejects.toMatchObject({ reason: 'signatureDeclined' });
        const draft = (await f.store.read([preparation])).sets[0]![0]!.value;
        const certificate = deviceCertificateCodec.decode(draft.certificate!);
        expect(certificate.pushGateway).toBe(pushGateway);
        f.network.clock.wall += 2; await f.dispose(); f.network.beforeSign = undefined;
        const resumed = await f.network.open(91, f.path);
        await resumed.client.establishAccount();
        expect(certificateId(resumed.client.device!, context)).toBe(certificateId(certificate, context));
        expect(resumed.client.device!.pushGateway).toBe(pushGateway);
        expect(resumed.client.device!.expiresAt).toBeGreaterThan(certificate.expiresAt);
        const stored = (await resumed.store.read([local])).sets[0]![0]!.value;
        expect(stored.protectedSigningKey).toBe(draft.protectedSigningKey);
        expect(stored.protectedEncryptionKey).toBe(draft.protectedEncryptionKey);
    });

    test('unfinished setup retains saved lifetimes and gateway when retry options change', async () => {
        const f = await fixture();
        f.network.beforeSign = async input => {
            if (requireObject(parseJson(input)).signing_public_key !== undefined) throw new DOMException('Declined', 'NotAllowedError');
        };
        await expect(f.client.establishAccount({ certificateValiditySeconds: 86400, routeValiditySeconds: 172800, pushGateway: gateway })).rejects.toMatchObject({ reason: 'signatureDeclined' });
        f.network.beforeSign = undefined; await f.dispose();
        const resumed = await f.network.open(91, f.path);
        await resumed.client.establishAccount({ certificateValiditySeconds: 864000, routeValiditySeconds: 1728000 });
        expect(resumed.client.device!.pushGateway).toBe(gateway);
        expect(resumed.client.device!.expiresAt - resumed.client.device!.notBefore).toBe(86400);
        expect(resumed.client.route!.expiresAt - resumed.client.route!.updatedAt).toBe(172800);
    });

    test.each([
        [false, 'failBefore'], [false, 'loseAfter'], [true, 'failBefore'], [true, 'loseAfter'],
    ] as const)('unknown update publication resumes the exact certificate (removed=%s, %s)', async (remove, failure) => {
        const f = await fixture(); await f.client.establishAccount({ pushGateway: gateway });
        const renewed = await f.client.deviceManager.renewDevice({ pushGateway: remove ? FieldUpdate.delete : nextGateway });
        f.network.requests.length = 0; f.network[failure] = 'device.state.publish';
        await expect(f.client.establishAccount()).rejects.toMatchObject({ reason: 'submissionOutcomeUnknown' });
        const exact = canonicalJson(f.network.requests.find(value => value.method === 'device.state.publish')!.body);
        expect((await f.store.read([request])).sets[0]![0]!.value.pending).toBe(true);
        await f.dispose(); f.network[failure] = undefined;
        const resumed = await f.network.open(91, f.path);
        await expect(resumed.client.deviceManager.renewDevice({ pushGateway: gateway })).rejects.toThrow();
        await resumed.client.establishAccount();
        expect(resumed.client.device).toEqual(renewed);
        expect(resumed.client.deviceState!.certificates).toContainEqual(renewed);
        expect(f.network.requests.filter(value => value.method === 'device.state.publish').every(value => canonicalJson(value.body) === exact)).toBe(true);
    });

    test.each(['new', 'valid', 'expired', 'removed'] as const)('recovery applies creation gateway only to a new device (%s)', async scenario => {
        const f = await fixture();
        if (scenario !== 'new') await f.client.establishAccount({ pushGateway: nextGateway });
        if (scenario === 'expired' || scenario === 'removed') {
            await f.client.deviceManager.renewDevice({ validitySeconds: 1, ...(scenario === 'removed' ? { pushGateway: FieldUpdate.delete } : {}) });
            await f.client.establishAccount(); f.network.clock.wall += 2;
        }
        const id = f.client.device && certificateId(f.client.device, context);
        await f.client.recoverAccount({ pushGateway: gateway });
        expect(f.client.device!.pushGateway).toBe(scenario === 'new' ? gateway : scenario === 'removed' ? undefined : nextGateway);
        if (id) expect(certificateId(f.client.device!, context)).toBe(id);
    });
});

describe('input validation', () => {
    test.each(['', 'http://push.example.com', null, 'https://a\u1fefb.example/callback', 'https://a\ufa6cb.example/callback'])('invalid renewal value %j has no signing, storage or session effects', async pushGateway => {
        const f = await fixture(); await f.client.establishAccount({ pushGateway: gateway });
        const session = await f.pool.get(f.relayId, { mode: 'device', signer: f.client.deviceManager });
        const before = await f.store.read([local]); const signatures = f.network.signatures.length; const requests = f.network.requests.length;
        await expect(f.client.deviceManager.renewDevice({ pushGateway: pushGateway as string })).rejects.toMatchObject({ code: 'invalid_push_gateway' });
        expect(await f.store.read([local])).toEqual(before);
        expect(f.network.signatures).toHaveLength(signatures); expect(f.network.requests).toHaveLength(requests);
        expect(session.isDisposed).toBe(false);
    });

    test.each(['create', 'establish', 'recover'].flatMap(operation =>
        ['http://push.example.com', 'https://a\u1fefb.example/callback', 'https://a\ufa6cb.example/callback'].map(pushGateway => ({ operation, pushGateway })),
    ))('$operation rejects invalid creation gateway $pushGateway before I/O', async ({ operation, pushGateway }) => {
        const f = await fixture(); const requests = f.network.requests.length; const signatures = f.network.signatures.length;
        const options = { pushGateway };
        const pending = operation === 'create' ? f.client.deviceManager.createDevice(options)
            : operation === 'establish' ? f.client.establishAccount(options) : f.client.recoverAccount(options);
        await expect(pending).rejects.toMatchObject({ code: 'invalid_push_gateway' });
        expect(f.network.requests).toHaveLength(requests);
        expect(f.network.signatures).toHaveLength(signatures);
        expect((await f.store.read([local, preparation, progress])).sets).toEqual([[], [], []]);
    });

    test.each(['create', 'establish', 'recover', 'renew'] as const)('Expo %s rejects IPv4-in-IPv6 leading zeros before signing or persistence', async operation => {
        const f = await fixture();
        if (operation === 'renew') await f.client.establishAccount({ pushGateway: gateway });
        const before = await f.store.read([local, preparation, progress]);
        const signatures = f.network.signatures.length; const requests = f.network.requests.length;
        const previous = f.client.device;
        vi.stubGlobal('URL', ExpoUrl);
        const options = { pushGateway: 'https://[::ffff:192.168.001.1]/callback' };
        const pending = operation === 'create' ? f.client.deviceManager.createDevice(options)
            : operation === 'renew' ? f.client.deviceManager.renewDevice(options)
                : operation === 'establish' ? f.client.establishAccount(options) : f.client.recoverAccount(options);
        await expect(pending).rejects.toMatchObject({ code: 'invalid_push_gateway' });
        expect(await f.store.read([local, preparation, progress])).toEqual(before);
        expect(f.network.signatures).toHaveLength(signatures); expect(f.network.requests).toHaveLength(requests);
        expect(f.client.device).toEqual(previous);
    });
});

describe('call isolation', () => {
    test.each(['create', 'establish', 'recover', 'renew'] as const)('%s snapshots caller options before its first await', async operation => {
        const f = await fixture();
        if (operation === 'renew') await f.client.establishAccount();
        const options = { pushGateway: gateway, validitySeconds: 86400, certificateValiditySeconds: 86400 };
        const pending = operation === 'create' ? f.client.deviceManager.createDevice(options)
            : operation === 'renew' ? f.client.deviceManager.renewDevice(options)
                : operation === 'recover' ? f.client.recoverAccount(options) : f.client.establishAccount(options);
        options.pushGateway = 'http://invalid.example.com'; options.validitySeconds = options.certificateValiditySeconds = 0;
        await pending;
        expect(f.client.device!.pushGateway).toBe(gateway);
        expect(f.client.device!.expiresAt - f.client.device!.notBefore).toBe(86400);
    });

    test.each(['keep', 'set', 'delete'] as const)('queued renewal %s snapshots options and uses the preceding committed declaration', async operation => {
        const f = await fixture(); await f.client.establishAccount({ pushGateway: gateway });
        let entered!: () => void; let release!: () => void;
        const ready = new Promise<void>(resolve => { entered = resolve; }); const wait = new Promise<void>(resolve => { release = resolve; });
        f.network.beforeSign = async input => { if (requireObject(parseJson(input)).signing_public_key !== undefined) { entered(); await wait; } };
        const first = f.client.deviceManager.renewDevice({ pushGateway: nextGateway });
        let second: ReturnType<typeof f.client.deviceManager.renewDevice> | undefined;
        const options: { validitySeconds: number; pushGateway: DeviceRenewalOptions['pushGateway'] } = {
            validitySeconds: 86400, pushGateway: operation === 'keep' ? undefined : operation === 'set' ? gateway : FieldUpdate.delete,
        };
        try {
            await ready;
            second = f.client.deviceManager.renewDevice(options);
            options.pushGateway = 'http://invalid.example.com'; options.validitySeconds = 0;
        } finally { release(); }
        await first; const renewed = await second!;
        expect(renewed.pushGateway).toBe(operation === 'keep' ? nextGateway : operation === 'set' ? gateway : undefined);
        expect(renewed.expiresAt - renewed.notBefore).toBe(86400);
    });
});
