import { afterEach, describe, expect, test, vi } from 'vitest';
import { FieldUpdate, MessageManager, certificateId, contactRecordCodec, contactGrantInput, decryptMessage, deviceCertificateCodec, messageSendRequestCodec, type JsonObject, type StoreMutation } from '@meshline/sdk';
import { MessageContacts } from '../../packages/sdk/dist/messages/contacts.js';
import { context } from '../support/relay-fixture.js';
import { MessagingNetwork } from '../support/messaging-network.js';

type Client = Awaited<ReturnType<MessagingNetwork['client']>>;
const networks: MessagingNetwork[] = [];
afterEach(async () => { for (const network of networks.splice(0)) await network.dispose(); });
function deferred() { let resolve!: () => void; const promise = new Promise<void>(done => { resolve = done; }); return { promise, resolve }; }
async function contact(client: Client, account: string) { return (await client.store.read([{ collection: 'contacts', key: account }])).sets[0]![0]!.value; }
async function putContact(client: Client, account: string, value: JsonObject) {
    const snapshot = await client.store.read([]);
    await client.store.commit(snapshot.version, [{ kind: 'put', collection: 'contacts', key: account, value }]);
}
async function connect(network: MessagingNetwork, a: Client, b: Client) {
    await a.messages.start(); await b.messages.start();
    const request = await a.messages.addContact(b.accountId);
    await vi.waitFor(async () => expect((await a.messages.getSendStatus(request.messageId))?.state).toBe('targetAccepted'), { timeout: 5000 });
    await b.messages.synchronize(network.descriptor.relayId); await b.messages.acceptContactRequest(a.accountId);
    await vi.waitFor(() => expect(network.submissions.some(value => value.envelope.from === b.accountId && value.envelope.to === a.accountId)).toBe(true), { timeout: 5000 });
    await a.messages.synchronize(network.descriptor.relayId);
    await a.messages.stop(); await b.messages.stop();
}
async function fixture(network = new MessagingNetwork()) {
    networks.push(network);
    const a = await network.client(21); const b = await network.client(22);
    await connect(network, a, b);
    const payloads = new WeakMap<Client, Map<string, JsonObject>>();
    const pollNotifications = (client: Client, recipient: string) => {
        let cache = payloads.get(client);
        if (!cache) { cache = new Map(); payloads.set(client, cache); }
        return notifications(client, recipient, cache);
    };
    return { network, a, b, pollNotifications };
}
function replacementManager(network: MessagingNetwork, client: Client) {
    const messages = new MessageManager({ context, accountId: client.accountId, store: client.store, relayClients: client.pool, accountManager: client.account,
        deviceManager: client.device, secretProtector: client.protector, clock: network.clock }); network.resources.push(messages); return messages;
}
// Polls reuse verified payloads; final assertions omit the cache and verify the current request again.
async function notifications(client: Client, recipient: string, payloads?: Map<string, JsonObject>) {
    const values: { id: string; revision: number; state: string; request: JsonObject }[] = [];
    const certificate = client.device.certificate; const deviceId = certificateId(certificate, context);
    const certificateKey = payloads && deviceCertificateCodec.stringify(certificate);
    for (const row of (await client.store.read([{ collection: 'message_outbox' }])).sets[0]!) {
        const request = messageSendRequestCodec.decode(row.value.request!);
        if (request.envelope.to !== recipient) continue;
        const key = payloads && JSON.stringify([certificateKey, row.value.request]);
        let payload = key === undefined ? undefined : payloads!.get(key);
        if (!payload) {
            const box = request.senderBoxes!.find(value => value.deviceId === deviceId)!;
            payload = await decryptMessage({ context, receiver: client.device, sender: certificate, envelope: request.envelope, keyBox: box });
            if (key !== undefined) payloads!.set(key, payload);
        }
        if (payload['$type'] === 'meshline.device.state.changed') values.push({ id: row.key, revision: payload.revision as number, state: row.value.state as string, request: row.value.request as JsonObject });
    }
    return values;
}
/** Observe durable notifications even when the outbox has not transmitted them yet. */
function observeDeviceNotifications(a: Client, recipient: string,
    afterFirst?: () => Promise<void>) {
    const revisions: number[] = []; const seen = new Set<string>(); const commit = a.store.commit.bind(a.store);
    const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        const result = await commit(version, mutations, signal);
        for (const mutation of mutations) {
            if (mutation.kind !== 'put' || mutation.collection !== 'message_outbox' || seen.has(mutation.key)) continue;
            const request = messageSendRequestCodec.decode(mutation.value.request!);
            if (request.envelope.to !== recipient) continue;
            seen.add(mutation.key);
            const box = request.senderBoxes?.find(value => value.deviceId === certificateId(a.device.certificate, context));
            if (!box) continue;
            const payload = await decryptMessage({ context, receiver: a.device, sender: a.device.certificate, envelope: request.envelope, keyBox: box });
            if (payload['$type'] !== 'meshline.device.state.changed') continue;
            revisions.push(payload.revision as number);
            if (revisions.length === 1) await afterFirst?.();
        }
        return result;
    });
    return { revisions, spy };
}

/** A completed scan is an explicit barrier for assertions that no notification was created. */
function observeScans(account: string) {
    let count = 0; const maintain = MessageContacts.prototype.maintain;
    const spy = vi.spyOn(MessageContacts.prototype, 'maintain').mockImplementation(async function* (this: MessageContacts, ...args) {
        yield* maintain.apply(this, args); if (this.host.accountId === account) count++;
    });
    return { spy, count: () => count, wait: async (previous: number) => { await vi.waitFor(() => expect(count).toBeGreaterThan(previous), { timeout: 5000 }); } };
}
function notificationCommit(mutations: readonly StoreMutation[], account: string, baseline: number) {
    return mutations.some(value => value.kind === 'put' && value.collection === 'contacts' && value.key === account && (value.value.deviceNotificationRevision as number) > baseline);
}

describe('Device-state notification lifecycle', () => {
    test('unchanged starts, foreground resumes, ordinary sends and reopened clients create no notifications', async () => {
        const { network, a, b } = await fixture();
        expect((await contact(a, b.accountId)).deviceNotificationRevision).toBe(a.device.deviceState!.revision);
        expect((await contact(b, a.accountId)).deviceNotificationRevision).toBe(b.device.deviceState!.revision);
        const scans = observeScans(a.accountId);
        for (let cycle = 0; cycle < 3; cycle++) {
            const before = scans.count(); await a.messages.start(); await scans.wait(before);
            await a.device.refreshDeviceState();
            const sent = await a.messages.sendMessage(b.accountId, { body: { contentType: 'text/plain', text: `ordinary ${cycle}` } });
            await vi.waitFor(async () => expect((await a.messages.getSendStatus(sent.messageId))?.state).toBe('targetAccepted'));
            await a.messages.stop(); expect(await notifications(a, b.accountId)).toEqual([]);
        }
        await a.dispose(); const reopened = await network.client(21, a.path);
        const before = scans.count(); await reopened.messages.start(); await scans.wait(before); await reopened.messages.stop();
        expect(await notifications(reopened, b.accountId)).toEqual([]);
    });

    test.each(['running', 'stopped', 'reopened'] as const)('device additions, removals, renewals and gateway updates notify while %s', async mode => {
        const { network, a, b, pollNotifications } = await fixture(); let current = a;
        const expected: number[] = []; let second: Client;
        const changes = [
            async () => { second = await network.client(21); await current.device.refreshDeviceState(); },
            async () => { await second.device.removeDevice(certificateId(second.device.certificate, context)); await current.device.refreshDeviceState(); },
            async () => { await current.device.renewDevice(7200); await current.device.publishDeviceState(network.descriptor.relayId); },
            async () => { await current.device.renewDevice({ validitySeconds: 7200, pushGateway: 'https://push.example/gateway' }); await current.device.publishDeviceState(network.descriptor.relayId); },
            async () => { await current.device.renewDevice({ validitySeconds: 7200, pushGateway: FieldUpdate.delete }); await current.device.publishDeviceState(network.descriptor.relayId); },
        ];
        if (mode === 'running') await current.messages.start();
        for (const change of changes) {
            await change(); expected.push(current.device.deviceState!.revision);
            if (mode === 'reopened') { await current.dispose(); current = await network.client(21, current.path); }
            await current.messages.start();
            await network.until(async () => (await pollNotifications(current, b.accountId)).filter(value => value.state === 'targetAccepted').length === expected.length);
            expect((await contact(current, b.accountId)).deviceNotificationRevision).toBe(expected.at(-1));
            if (mode !== 'running') await current.messages.stop();
        }
        await current.messages.stop();
        expect((await notifications(current, b.accountId)).map(value => value.revision).sort((a, b) => a - b)).toEqual(expected);
    });

    test('a revision published before startup subscribes is included by the initial scan', async () => {
        const { network, a, b, pollNotifications } = await fixture(); const entered = deferred(); const release = deferred();
        const commit = a.store.commit.bind(a.store); let blocked = false;
        const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            const result = await commit(version, mutations, signal);
            if (!blocked && mutations.some(value => value.kind === 'put' && value.collection === 'message_outbox')) {
                blocked = true; entered.resolve(); await release.promise;
            }
            return result;
        });
        const starting = a.messages.start();
        try {
            await entered.promise; await a.device.publishDeviceState(network.descriptor.relayId); release.resolve(); await starting;
            await network.until(async () => (await pollNotifications(a, b.accountId)).some(value => value.state === 'targetAccepted'));
            expect((await notifications(a, b.accountId)).map(value => value.revision)).toEqual([a.device.deviceState!.revision]);
        } finally { release.resolve(); await starting; await a.messages.stop(); spy.mockRestore(); }
    });

    test('a newer revision invalidates a prepared notification without advancing its checkpoint', async () => {
        const { network, a, b } = await fixture(); const baseline = a.device.deviceState!.revision;
        await a.device.publishDeviceState(network.descriptor.relayId); const superseded = a.device.deviceState!.revision;
        const entered = deferred(); const release = deferred(); const commit = a.store.commit.bind(a.store); let blocked = false;
        const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            if (!blocked && notificationCommit(mutations, b.accountId, baseline)) { blocked = true; entered.resolve(); await release.promise; }
            return commit(version, mutations, signal);
        });
        try {
            await a.messages.start(); await entered.promise;
            expect((await contact(a, b.accountId)).deviceNotificationRevision).toBe(baseline);
            await a.device.publishDeviceState(network.descriptor.relayId); release.resolve();
            await network.until(async () => (await contact(a, b.accountId)).deviceNotificationRevision === a.device.deviceState!.revision);
            const revisions = (await notifications(a, b.accountId)).map(value => value.revision);
            expect(revisions).toEqual([a.device.deviceState!.revision]); expect(revisions).not.toContain(superseded);
        } finally { release.resolve(); await a.messages.stop(); spy.mockRestore(); }
    });

    test('a completed old maintenance pass cannot suppress notification of a newer device revision', async () => {
        const { a, b, network } = await fixture();
        await a.device.publishDeviceState(network.descriptor.relayId);
        const entered = deferred(); const release = deferred();
        const observation = observeDeviceNotifications(a, b.accountId, async () => { entered.resolve(); await release.promise; });
        try {
            await a.messages.start(); await entered.promise;
            const previous = a.device.deviceState!.revision;
            await a.device.publishDeviceState(network.descriptor.relayId);
            const revision = a.device.deviceState!.revision; expect(revision).toBeGreaterThan(previous);
            release.resolve();
            await vi.waitFor(() => expect(observation.revisions).toContain(revision));
            expect(observation.revisions).toContain(previous);
        } finally { release.resolve(); await a.messages.stop(); observation.spy.mockRestore(); }
    });
});

describe('Durable notification recovery', () => {
    test('restart reconciles a newer revision even when another contact failed maintenance', async () => {
        const { a, b, network } = await fixture();
        const other = await network.client(23); const id = certificateId(a.device.certificate, context);
        let grant = { grantor: a.accountId, grantee: other.accountId, signatures: {} };
        grant = { ...grant, signatures: { [id]: await a.device.sign(contactGrantInput(grant, context)) } };
        const snapshot = await a.store.read([]);
        await a.store.commit(snapshot.version, [{ kind: 'put', collection: 'contacts', key: other.accountId, value: {
            record: contactRecordCodec.encode({ account: other.accountId, status: 'active', updatedAt: network.clock.wall, grantToContact: grant }),
        } }]);
        const failed: string[] = []; const detach = a.messages.onLifecycle('backgroundError', value => { if (value.operation === 'maintain_contact') failed.push(value.resource!); });
        const observation = observeDeviceNotifications(a, b.accountId);
        try {
            await a.device.publishDeviceState(network.descriptor.relayId);
            await a.messages.start();
            await vi.waitFor(() => { expect(failed).toContain(other.accountId); expect(observation.revisions).toContain(a.device.deviceState!.revision); });
            await a.messages.stop(); await a.device.publishDeviceState(network.descriptor.relayId);
            const revision = a.device.deviceState!.revision;
            await a.messages.start(); await vi.waitFor(() => expect(observation.revisions).toContain(revision));
        } finally { await a.messages.stop(); observation.spy.mockRestore(); detach(); }
    });

    test.each([
        { code: 'rate_limited', status: 429, retryAfter: 15, refreshRoute: false },
        { code: 'route_stale', status: 400, retryAfter: undefined, refreshRoute: true },
        { code: 'target_not_local', status: 400, retryAfter: undefined, refreshRoute: true },
        { code: 'unauthorized', status: 401, retryAfter: undefined, refreshRoute: false },
    ].flatMap(rejection => ['retry', 'reopen'].map(recovery => ({ ...rejection, recovery }))))(
        'a $code notification resumes on $recovery without duplicating successful contacts', async ({ code, status, retryAfter, refreshRoute, recovery }) => {
        const network = new MessagingNetwork(); const fetch = vi.spyOn(network, 'fetch');
        const { a, b, pollNotifications } = await fixture(network); const c = await network.client(23); await connect(network, a, c);
        await a.device.publishDeviceState(network.descriptor.relayId);
        const authenticationCount = () => fetch.mock.calls.filter(([url, init]) => String(url).endsWith('/auth/device/verify')
            && JSON.parse(String(init?.body)).signer_certificate?.account === a.accountId).length;
        let authentications = 0;
        const refresh = vi.spyOn(a.account, 'refreshRoute');
        const requests: string[] = []; let rejectRequests = true; let current = a;
        network.handleRequest = async (method, body, caller) => {
            if (method !== 'message.send' || caller !== a.accountId) return undefined;
            const request = messageSendRequestCodec.decode(body); if (request.envelope.to !== b.accountId) return undefined;
            const box = request.senderBoxes!.find(value => value.deviceId === certificateId(current.device.certificate, context))!;
            const payload = await decryptMessage({ context, receiver: current.device, sender: current.device.certificate, envelope: request.envelope, keyBox: box });
            if (payload['$type'] !== 'meshline.device.state.changed') return undefined;
            requests.push(messageSendRequestCodec.stringify(request));
            if (!rejectRequests) return undefined;
            if (code === 'unauthorized') authentications = authenticationCount();
            return new Response(JSON.stringify({ code, message: 'rejected', ...(retryAfter === undefined ? {} : { data: { retry_after: retryAfter } }) }),
                { status, headers: { 'content-type': 'application/json', ...(retryAfter === undefined ? {} : { 'retry-after': String(retryAfter) }) } });
        };
        await a.messages.start();
        await network.until(async () => {
            const pending = (await pollNotifications(a, b.accountId))[0]; if (!pending) return false;
            const row = (await a.store.read([{ collection: 'message_outbox', key: pending.id }])).sets[0]![0]!;
            return row.value.state === 'queued' && row.value.errorMessage === 'rejected'
                && (await pollNotifications(a, c.accountId)).some(value => value.state === 'targetAccepted');
        });
        if (refreshRoute) await vi.waitFor(() => expect(refresh).toHaveBeenCalled());
        const original = (await notifications(a, b.accountId))[0]!; const successful = (await notifications(a, c.accountId))[0]!;
        expect((await contact(a, b.accountId)).deviceNotificationRevision).toBe(a.device.deviceState!.revision);
        rejectRequests = false;
        if (recovery === 'reopen') { await a.dispose(); current = await network.client(21, a.path); await current.messages.start(); }
        await network.until(async () => (await pollNotifications(current, b.accountId)).some(value => value.state === 'targetAccepted'));
        await current.messages.stop();
        expect(await notifications(current, b.accountId)).toEqual([{ ...original, state: 'targetAccepted' }]);
        expect(await notifications(current, c.accountId)).toEqual([successful]);
        expect(requests.length).toBeGreaterThanOrEqual(2); expect(requests.every(value => value === messageSendRequestCodec.stringify(messageSendRequestCodec.decode(original.request)))).toBe(true);
        if (code === 'unauthorized') expect(authenticationCount()).toBeGreaterThan(authentications);
    });

    test.each(['retry', 'reopen'] as const)('a failed contact resumes on %s without recreating the successful contact notification', async recovery => {
        const { network, a, b, pollNotifications } = await fixture(); const c = await network.client(23); await connect(network, a, c);
        const baseline = a.device.deviceState!.revision; await a.device.publishDeviceState(network.descriptor.relayId);
        const commit = a.store.commit.bind(a.store); const failure = new Error('Notification transaction unavailable');
        const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            if (notificationCommit(mutations, c.accountId, baseline)) throw failure;
            return commit(version, mutations, signal);
        });
        const failures: unknown[] = []; const detach = a.messages.onLifecycle('backgroundError', value => { if (value.resource === c.accountId) failures.push(value.error); });
        await a.messages.start();
        await vi.waitFor(async () => { expect(failures).toContain(failure); expect(await pollNotifications(a, b.accountId)).toHaveLength(1); });
        expect((await contact(a, c.accountId)).deviceNotificationRevision).toBe(baseline); expect(await notifications(a, c.accountId)).toEqual([]);
        const successful = (await notifications(a, b.accountId))[0]!; let current = a;
        spy.mockRestore(); detach();
        if (recovery === 'reopen') { await a.dispose(); current = await network.client(21, a.path); }
        await current.messages.start();
        await network.until(async () => (await pollNotifications(current, c.accountId)).some(value => value.state === 'targetAccepted'));
        expect((await notifications(current, b.accountId)).map(value => value.id)).toEqual([successful.id]);
        expect((await notifications(current, c.accountId)).map(value => value.revision)).toEqual([current.device.deviceState!.revision]);
    });

    test('stopping during notification preparation leaves it pending for the next client', async () => {
        const { network, a, b, pollNotifications } = await fixture(); const baseline = a.device.deviceState!.revision;
        await a.device.publishDeviceState(network.descriptor.relayId); const entered = deferred(); const commit = a.store.commit.bind(a.store);
        const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            if (notificationCommit(mutations, b.accountId, baseline)) {
                entered.resolve(); await new Promise<void>((_, reject) => {
                    signal!.throwIfAborted(); signal!.addEventListener('abort', () => reject(signal!.reason), { once: true });
                });
            }
            return commit(version, mutations, signal);
        });
        await a.messages.start(); await entered.promise; await a.messages.stop(); spy.mockRestore();
        expect((await contact(a, b.accountId)).deviceNotificationRevision).toBe(baseline); expect(await notifications(a, b.accountId)).toEqual([]);
        await a.dispose(); const reopened = await network.client(21, a.path); await reopened.messages.start();
        await network.until(async () => (await pollNotifications(reopened, b.accountId)).some(value => value.state === 'targetAccepted'));
        expect(await notifications(reopened, b.accountId)).toHaveLength(1);
    });

    test('unavailable contact authorization preserves the revision for a later retry', async () => {
        const { network, a, b, pollNotifications } = await fixture(); const saved = await contact(a, b.accountId);
        const { grantFromContact, ...record } = contactRecordCodec.decode(saved.record!);
        await putContact(a, b.accountId, { ...saved, record: contactRecordCodec.encode(record) });
        await a.device.publishDeviceState(network.descriptor.relayId); await a.messages.start();
        await vi.waitFor(() => expect(a.messages.lastBackgroundError?.error).toMatchObject({ code: 'contact_grant_required' }));
        await a.messages.stop();
        expect((await contact(a, b.accountId)).deviceNotificationRevision).toBe(saved.deviceNotificationRevision);
        expect(await notifications(a, b.accountId)).toEqual([]);
        await putContact(a, b.accountId, saved); await a.messages.start();
        await network.until(async () => (await pollNotifications(a, b.accountId)).some(value => value.state === 'targetAccepted'));
        expect((await notifications(a, b.accountId)).map(value => value.revision)).toEqual([a.device.deviceState!.revision]);
    });

    test('unknown delivery reuses its exact request after reopen, including after completed outbox retention', async () => {
        const { network, a, b, pollNotifications } = await fixture(); await a.device.publishDeviceState(network.descriptor.relayId);
        network.loseResponse = true; await a.messages.start();
        await vi.waitFor(async () => expect((await pollNotifications(a, b.accountId)).some(value => value.state === 'submissionUnknown')).toBe(true));
        await a.messages.stop(); const original = (await notifications(a, b.accountId))[0]!;
        await a.dispose(); network.loseResponse = false; const reopened = await network.client(21, a.path); await reopened.messages.start();
        await network.until(async () => (await pollNotifications(reopened, b.accountId)).some(value => value.state === 'targetAccepted'));
        await reopened.messages.stop();
        expect(await notifications(reopened, b.accountId)).toEqual([{ ...original, state: 'targetAccepted' }]);
        const snapshot = await reopened.store.read([]);
        await reopened.store.commit(snapshot.version, [{ kind: 'delete', collection: 'message_outbox', key: original.id }]);
        const scans = observeScans(a.accountId); await reopened.messages.start(); await scans.wait(0); await reopened.messages.stop();
        expect(await notifications(reopened, b.accountId)).toEqual([]);
    });
});

describe('Notification baseline upgrade', () => {
    test('older contacts retain metadata and pending requests while initialization establishes the cached baseline', async () => {
        const { network, a, b, pollNotifications } = await fixture(); await a.messages.dispose();
        const { deviceNotificationRevision, ...legacy } = await contact(a, b.accountId);
        await putContact(a, b.accountId, legacy);
        const before = (await a.store.read([{ collection: 'message_outbox' }])).sets;
        const messages = replacementManager(network, a);
        await messages.initialize();
        expect(await contact(a, b.accountId)).toEqual({ ...legacy, deviceNotificationRevision: a.device.deviceState!.revision });
        expect((await a.store.read([{ collection: 'message_outbox' }])).sets).toEqual(before);
        const scans = observeScans(a.accountId); await messages.start(); await scans.wait(0); await messages.stop();
        expect(await notifications(a, b.accountId)).toEqual([]);
        await a.device.publishDeviceState(network.descriptor.relayId); await messages.start();
        await network.until(async () => (await pollNotifications(a, b.accountId)).some(value => value.state === 'targetAccepted'));
        expect((await notifications(a, b.accountId)).map(value => value.revision)).toEqual([a.device.deviceState!.revision]);
    });

    test('a revision change between initialization and first start is not absorbed into the baseline', async () => {
        const { network, a, b, pollNotifications } = await fixture(); await a.dispose();
        const reopened = await network.client(21, a.path); const baseline = (await contact(reopened, b.accountId)).deviceNotificationRevision;
        await reopened.device.publishDeviceState(network.descriptor.relayId);
        expect((await contact(reopened, b.accountId)).deviceNotificationRevision).toBe(baseline);
        await reopened.messages.start();
        await network.until(async () => (await pollNotifications(reopened, b.accountId)).some(value => value.state === 'targetAccepted'));
        expect((await notifications(reopened, b.accountId)).map(value => value.revision)).toEqual([reopened.device.deviceState!.revision]);
    });

    test('baseline initialization preserves device changes across a conflicting commit', async () => {
        const { network, a, b, pollNotifications } = await fixture(); await a.messages.dispose();
        const baseline = a.device.deviceState!.revision;
        const { deviceNotificationRevision, ...legacy } = await contact(a, b.accountId);
        await putContact(a, b.accountId, legacy);
        const commit = a.store.commit.bind(a.store); let raced = false;
        const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            if (!raced && mutations.some(value => value.kind === 'put' && value.collection === 'contacts' && value.value.deviceNotificationRevision === baseline)) {
                raced = true;
                await a.device.renewDevice({ validitySeconds: 7200, pushGateway: 'https://push.example/updated' });
                await a.device.publishDeviceState(network.descriptor.relayId);
            }
            return commit(version, mutations, signal);
        });
        const messages = replacementManager(network, a);
        try { await messages.initialize(); } finally { spy.mockRestore(); }
        expect(raced).toBe(true); expect(a.device.deviceState!.revision).toBeGreaterThan(baseline);
        expect(await contact(a, b.accountId)).toEqual({ ...legacy, deviceNotificationRevision: baseline });
        expect(await notifications(a, b.accountId)).toEqual([]);
        await messages.start();
        await network.until(async () => (await pollNotifications(a, b.accountId)).some(value => value.state === 'targetAccepted'));
        await messages.stop();
        expect((await notifications(a, b.accountId)).map(value => value.revision)).toEqual([a.device.deviceState!.revision]);
        expect((await contact(a, b.accountId)).deviceNotificationRevision).toBe(a.device.deviceState!.revision);
        expect(messages.lastBackgroundError).toBeUndefined();
    });

    test('baseline initialization retries storage contention without overwriting concurrent contact edits', async () => {
        const { network, a, b } = await fixture(); await a.messages.dispose();
        const { deviceNotificationRevision, ...legacy } = await contact(a, b.accountId); await putContact(a, b.accountId, legacy);
        const edited = { ...legacy, record: { ...legacy.record as JsonObject, alias: 'concurrent alias' } };
        const commit = a.store.commit.bind(a.store); let raced = false;
        const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (version, mutations, signal) => {
            if (!raced && mutations.some(value => value.collection === 'contacts')) {
                raced = true; await commit(version, [{ kind: 'put', collection: 'contacts', key: b.accountId, value: edited }], signal);
            }
            return commit(version, mutations, signal);
        });
        const messages = replacementManager(network, a); await messages.initialize(); spy.mockRestore();
        expect(raced).toBe(true);
        expect(await contact(a, b.accountId)).toEqual({ ...edited, deviceNotificationRevision: a.device.deviceState!.revision });
        expect(await notifications(a, b.accountId)).toEqual([]);
    });

    test('startup fetching an uncached own state still compares it with the retained contact checkpoint', async () => {
        const { network, a, b, pollNotifications } = await fixture(); const baseline = a.device.deviceState!.revision;
        await network.client(21);
        const snapshot = await a.store.read([]); await a.store.commit(snapshot.version, [{ kind: 'delete', collection: 'device_states', key: a.accountId }]);
        await a.dispose(); const reopened = await network.client(21, a.path);
        expect(reopened.device.deviceState).toBeUndefined();
        expect((await contact(reopened, b.accountId)).deviceNotificationRevision).toBe(baseline);
        await reopened.messages.start();
        await network.until(async () => (await pollNotifications(reopened, b.accountId)).some(value => value.state === 'targetAccepted'));
        expect((await notifications(reopened, b.accountId)).map(value => value.revision)).toEqual([network.states.get(a.accountId)!.revision]);
    });
});
