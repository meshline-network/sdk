import * as sdk from '@meshline/sdk';
import { context, relayPrivateKey, signedDescriptor, TestRegistry } from './relay-fixture.js';
import { MessageRepository } from '../../packages/sdk/dist/messages/repository.js';
import { MessageOutbox } from '../../packages/sdk/dist/messages/outbox.js';
import { recoverGroup, stageGroup } from './groups.js';

/** Shared acceptance: public test identity, in-memory relay, real adapter and SDK cryptography.
 * Reopens storage within one process; a separate fixture verifies native process restart. */
export async function managerRoundtrip({ createStore, random = sdk.systemRandom }: { createStore(): sdk.MeshlineStore; random?: sdk.RandomSource }): Promise<{ retriedOriginal: boolean; recoveredKey: boolean; nickname: string | undefined; pending: boolean; protectedKeys: boolean; recoveredMessage: boolean; retriedEncryptedMessage: boolean; recoveredChannel: boolean; channelPublishedOnce: boolean; recoveredGroup: boolean; protectedGroupSecrets: boolean; recoveredGroupAccountSync: boolean; failedGroupSyncUnchanged: boolean; groupKeyCursorAtomic: boolean; aliasSnapshot: boolean; aliasNoop: boolean }> {
    const key = new Uint8Array(32).fill(3); const publicKey = sdk.accountPublicKey(key); const accountId = sdk.getAccountId('neo:860833102', publicKey);
    const signer: sdk.AccountSigner = { accountId, publicKey, sign: async input => sdk.signAccount(input, key, random) };
    const descriptor = signedDescriptor(random);
    let route: sdk.AccountRoute | undefined; let deviceState: sdk.AccountDeviceState | undefined; let profile: sdk.AccountProfile | undefined;
    let loseProfileResponse = true; const publications: string[] = [];
    let channel: sdk.ChannelDescriptor | undefined; let loseChannelHistory = true; const channelEvents: sdk.ChannelEvent[] = []; const channelPublications: string[] = [];
    const response = (body: unknown, status = 200): Response => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
    const fetch: sdk.RelayFetch = async (url, init) => {
        const method = new URL(url).pathname.replace(/^\/v1/, '').slice(1).replaceAll('/', '.'); const now = Math.floor(Date.now() / 1000);
        if (method === 'relay.descriptor') return response(sdk.relayDescriptorCodec.encode(descriptor));
        if (method === 'auth.challenge') return response({ nonce: 'browser-challenge', created_at: now, expires_at: now + 300 });
        if (method === 'auth.account.verify' || method === 'auth.device.verify') { const mode = method.includes('account') ? 'account' : 'device'; return response({ token: mode, mode, expires_at: now + 3600 }); }
        if (method === 'account.route.publish') {
            route = sdk.accountRouteCodec.parse(init.body!); route = { ...route, relaySignature: sdk.signAccount(sdk.routeRelayInput(route, context), relayPrivateKey, random) };
            return response(sdk.accountRouteCodec.encode(route));
        }
        if (method === 'account.route.resolve') return route ? response(sdk.accountRouteCodec.encode(route)) : response({ code: 'not_found', message: 'Unknown' }, 404);
        if (method === 'device.state.publish') { deviceState = sdk.accountDeviceStateCodec.parse(init.body!); return response({ status: 'accepted' }); }
        if (method === 'device.state.resolve') return response(sdk.accountDeviceStateCodec.encode(deviceState!));
        if (method === 'profile.resolve') return profile ? response(sdk.profileResolveResultCodec.encode({ profile, signerCertificate: deviceState!.certificates[0]! })) : response({ code: 'not_found', message: 'Unknown' }, 404);
        if (method === 'profile.publish') {
            publications.push(init.body!); profile = sdk.accountProfileCodec.parse(init.body!);
            if (loseProfileResponse) throw new TypeError('Test relay lost the publication response'); return new Response(null, { status: 204 });
        }
        if (method.startsWith('channel.')) {
            const query = init.body ? sdk.requireObject(sdk.parseJson(init.body)) : Object.fromEntries(new URL(url).searchParams) as sdk.JsonObject;
            if (method === 'channel.resolve') return channel && (query.revision === undefined || Number(query.revision) === channel.revision)
                ? response(sdk.channelResolveResultCodec.encode({ descriptor: channel, signerCertificate: deviceState!.certificates[0]! })) : response({ code: 'not_found', message: 'Unknown channel' }, 404);
            if (method === 'channel.read') {
                if (loseChannelHistory) throw new TypeError('Channel history unavailable');
                return response(sdk.channelReadPageCodec.encode({ events: channelEvents.filter(event => query.after === undefined || event.sequence > Number(query.after)), certificates: deviceState!.certificates, hasMore: false }));
            }
            const payload = sdk.channelPayloadCodec.decode(query); sdk.validateChannelPayload(payload, context);
            if (payload.kind === 'descriptor') channel = payload.value; else channelPublications.push(init.body!);
            const event: sdk.ChannelEvent = { sequence: channelEvents.length, descriptorRev: channel!.revision, acceptedAt: now, signerDeviceId: sdk.certificateId(deviceState!.certificates[0]!, context), payload };
            sdk.verifyChannelEvent(event, deviceState!.certificates[0]!, channel!, context, channel!); channelEvents.push(event);
            return payload.kind === 'post' ? response({ sequence: event.sequence }) : new Response(null, { status: 204 });
        }
        throw new Error(`Unexpected browser fixture method ${method}`);
    };
    // Test-only master key survives the simulated restart; production protection belongs to the application.
    const master = new Uint8Array(32).fill(99);
    const protector: sdk.SecretProtector = {
        async protect(bytes, purpose) { const nonce = random.bytes(12); return sdk.concatBytes(nonce, sdk.encryptAes(master, nonce, bytes, sdk.encodeUtf8(purpose))); },
        async unprotect(bytes, purpose) { return sdk.decryptAes(master, bytes.slice(0, 12), bytes.slice(12), sdk.encodeUtf8(purpose)); },
    };
    async function open(migrate: boolean) {
        const store = createStore(); if (migrate) await store.migrate();
        const pool = new sdk.RelayClientPool({ context, accountId, fetch, random }, new TestRegistry(random));
        const account = new sdk.AccountManager({ context, accountId, store, relayClients: pool, accountSigner: signer }); await account.initialize();
        const device = new sdk.DeviceManager({ context, accountId, store, relayClients: pool, accountManager: account, accountSigner: signer, secretProtector: protector, random }); await device.initialize();
        const profiles = new sdk.ProfileManager({ context, accountId, store, relayClients: pool, accountManager: account, deviceSigner: device }); await profiles.initialize();
        const channels = new sdk.ChannelManager({ context, accountId, store, relayClients: pool, deviceManager: device, random }); await channels.initialize();
        const messages = new sdk.MessageManager({ context, accountId, store, relayClients: pool, accountManager: account, deviceManager: device, secretProtector: protector, random }); await messages.initialize();
        return { store, pool, account, device, profiles, channels, messages, async dispose() { await channels.dispose(); await profiles.dispose(); await messages.dispose(); await device.dispose(); await account.dispose(); await pool.dispose(); await store.dispose(); } };
    }
    const first = await open(true);
    let encrypted: sdk.MessageSendRequest | undefined; const messageSubmissions: string[] = [];
    let stagedGroup: Awaited<ReturnType<typeof stageGroup>>;
    const contactAccount = sdk.getAccountId('neo:860833102', sdk.accountPublicKey(new Uint8Array(32).fill(4)));
    let aliasSnapshot = false; let aliasNoop = false;
    try {
        const certificate = await first.device.createDevice(3600); await first.account.publishRoute(descriptor.relayId, { validitySeconds: 3600 });
        await first.device.publishDeviceState(descriptor.relayId, { certificates: [certificate] });
        // Seed one active contact for this adapter contract check. Contact consent
        // itself is covered by the separate public .NET interoperability fixtures.
        const contactVersion = await first.store.read([]);
        await first.store.commit(contactVersion.version, [{ kind: 'put', collection: 'contacts', key: contactAccount,
            value: { record: sdk.contactRecordCodec.encode({ account: contactAccount, status: 'active', updatedAt: Math.floor(Date.now() / 1000) }) } }]);
        const aliased = await first.messages.setContactAlias(contactAccount, '平台别名 😀');
        aliasSnapshot = aliased?.accountId === contactAccount && aliased.alias === '平台别名 😀' && aliased.state === 'active';
        const beforeNoop = await first.store.read([]); let aliasEvents = 0;
        const detachAlias = first.messages.on('contactChanged', () => { aliasEvents++; });
        try {
            const unchanged = await first.messages.setContactAlias(contactAccount, '平台别名 😀');
            aliasNoop = (await first.store.read([])).version === beforeNoop.version && aliasEvents === 0 && JSON.stringify(aliased) === JSON.stringify(unchanged);
        } finally { detachAlias(); }
        stagedGroup = await stageGroup({ store: first.store, device: first.device, accountMessages: first.messages, protector, random }, descriptor.relayId);
        try { await first.profiles.updateProfile({ nickname: '浏览器 😀' }); throw new Error('Expected a lost response'); }
        catch (error) { if (!(error instanceof TypeError) || !error.message.includes('lost')) throw error; }
        const payload = { $type: 'meshline.message.direct', body: { content_type: 'text/plain', text: '持久化加密消息 😀' } };
        encrypted = await sdk.encryptMessage({ context, signer: first.device, messageId: sdk.createIdentifier('message', random), createdAt: Math.floor(Date.now() / 1000),
            recipient: accountId, recipientDevices: [certificate], payload, random });
        const repository = new MessageRepository({ store: first.store, context, accountId, deviceId: () => sdk.certificateId(first.device.certificate, context), clock: sdk.systemClock });
        await repository.enqueue({ request: encrypted, relayId: descriptor.relayId, state: 'queued', nextAttemptAt: 0, isDirect: true }, await repository.prepare(encrypted.envelope, payload), []);
        const queue = new MessageOutbox(first.store, sdk.systemClock, {
            getHome: async () => descriptor.relayId, currentHome: () => descriptor.relayId,
            prepare: async () => ({ send: async request => { messageSubmissions.push(sdk.messageSendRequestCodec.stringify(request)); throw new TypeError('Lost message response'); }, status: async () => { throw new Error('Unexpected status request'); } }),
        });
        if (!(await queue.process(encrypted.envelope.messageId)).error) throw new Error('Expected uncertain message submission');
        const created = await first.channels.createChannel(descriptor.relayId, '浏览器频道 😀');
        try { await first.channels.publishPost(created.ref, { body: { contentType: 'text/plain', text: '浏览器频道恢复' } }); throw new Error('Expected unavailable channel history'); }
        catch (error) { if (!(error instanceof TypeError) || !error.message.includes('history unavailable')) throw error; }
    } finally { await first.dispose(); }
    loseProfileResponse = false;
    loseChannelHistory = false;
    const resumed = await open(false);
    try {
        await resumed.profiles.updateProfile({ nickname: '浏览器 😀' });
        const message = sdk.encodeUtf8('recovered native browser device key');
        const recoveredKey = sdk.verifyDevice(message, await resumed.device.sign(message), resumed.device.certificate.signingPublicKey);
        const snapshot = await resumed.store.read([{ collection: 'signed_requests', key: 'profile.publish' }, { collection: 'local_device', key: 'current' }]);
        const plaintext = await sdk.decryptMessage({ context, receiver: resumed.device, sender: resumed.device.certificate, envelope: encrypted!.envelope, keyBox: encrypted!.recipientBoxes[0]! });
        const repository = new MessageRepository({ store: resumed.store, context, accountId, deviceId: () => sdk.certificateId(resumed.device.certificate, context), clock: sdk.systemClock });
        const history = await repository.get({ sender: accountId, messageId: encrypted!.envelope.messageId });
        const retryClock = { ...sdk.systemClock, nowSeconds: () => Math.floor(Date.now() / 1000) + 16 };
        const queue = new MessageOutbox(resumed.store, retryClock, {
            getHome: async () => descriptor.relayId, currentHome: () => descriptor.relayId,
            prepare: async () => ({ send: async request => { messageSubmissions.push(sdk.messageSendRequestCodec.stringify(request)); return { status: 'target_accepted', acceptedAt: encrypted!.envelope.createdAt }; }, status: async () => { throw new Error('Unexpected status request'); } }),
        });
        await queue.recover(); const delivery = await queue.process(encrypted!.envelope.messageId); if (delivery.error) throw delivery.error;
        await resumed.channels.start();
        for (let attempts = 0; (await resumed.store.read([{ collection: 'channel_operations' }])).sets[0]!.length; attempts++) {
            if (attempts >= 200) throw new Error('Browser channel recovery did not complete'); await new Promise(resolve => setTimeout(resolve, 10));
        }
        await resumed.channels.stop(); const posts = await resumed.channels.getPosts(); let recoveredChannel: boolean;
        try { recoveredChannel = (await posts.readNext(10))[0]?.body?.text === '浏览器频道恢复'; } finally { await posts.dispose(); }
        return { retriedOriginal: publications.length === 2 && publications[0] === publications[1], recoveredKey,
            aliasSnapshot: aliasSnapshot && (await resumed.messages.getContact(contactAccount))?.alias === '平台别名 😀', aliasNoop,
            nickname: resumed.profiles.profile?.nickname, pending: snapshot.sets[0]![0]!.value.pending === true,
            protectedKeys: typeof snapshot.sets[1]![0]!.value.protectedSigningKey === 'string',
            recoveredMessage: history?.body?.text === '持久化加密消息 😀' && sdk.directMessageCodec.decode(plaintext).body?.text === history.body.text,
            retriedEncryptedMessage: messageSubmissions.length === 2 && messageSubmissions[0] === messageSubmissions[1]
                && (await queue.get(encrypted!.envelope.messageId))?.state === 'targetAccepted'
                && (await resumed.messages.getSendStatus(encrypted!.envelope.messageId))?.state === 'targetAccepted',
            recoveredChannel, channelPublishedOnce: channelPublications.length === 1,
            ...await recoverGroup({ store: resumed.store, device: resumed.device, accountMessages: resumed.messages, protector, random }, stagedGroup!) };
    } finally { await resumed.dispose(); master.fill(0); }
}
