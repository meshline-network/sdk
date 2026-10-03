import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import * as sdk from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { context, relayPrivateKey, signedDescriptor, TestRegistry } from './relay-fixture.js';
import { MessagingClock } from '../portable/clock.js';
export { MessagingClock } from '../portable/clock.js';
import { removeTestDirectory } from './temp.js';

interface Delivery { request: sdk.MessageSendRequest; certificate: sdk.DeviceCertificate; acceptedAt: number }
interface TimelineItem { readonly delivery: Delivery; readonly sequence: number; readonly sender: boolean }
export class MessagingNetwork {
    readonly clock = new MessagingClock(); readonly descriptor: sdk.RelayDescriptor;
    constructor(endpoint?: string) {
        const defaults = signedDescriptor().endpoints.filter(value => !value.startsWith('wss://'));
        this.descriptor = signedDescriptor({ endpoints: endpoint ? [endpoint, ...defaults.filter(value => value.startsWith('/'))] : defaults });
    }
    readonly routes = new Map<string, sdk.AccountRoute>(); readonly states = new Map<string, sdk.AccountDeviceState>();
    readonly deliveries = new Map<string, Delivery>(); readonly timelines = new Map<string, TimelineItem[]>(); readonly submissions: sdk.MessageSendRequest[] = [];
    readonly resources: { dispose(): Promise<void> }[] = []; readonly directories: string[] = [];
    loseResponse = false; resolveFailure: string | undefined; beforeSend: ((request: sdk.MessageSendRequest) => void | Promise<void>) | undefined;
    deliveryStatus: 'delivering' | 'target_accepted' = 'target_accepted';
    handleRequest: ((method: string, body: sdk.JsonObject, account: string, deviceId: string) => Promise<Response | undefined>) | undefined;
    readonly fetch: sdk.RelayFetch = async (url, init) => {
        const target = new URL(url); const prefix = new URL(this.descriptor.endpoints[0]!).pathname;
        const path = target.pathname.startsWith(prefix + '/') ? target.pathname.slice(prefix.length) : target.pathname;
        const method = path.slice(1).replaceAll('/', '.');
        const body = init.body ? sdk.requireObject(sdk.parseJson(init.body)) : Object.fromEntries(target.searchParams) as sdk.JsonObject;
        const response = (value: unknown, status = 200): Response => new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
        const absent = (): Response => response({ code: 'not_found', message: 'Unknown fixture state' }, 404);
        if (method === 'relay.descriptor') return response(sdk.relayDescriptorCodec.encode(this.descriptor));
        if (method === 'relay.info') return response({ relay_id: this.descriptor.relayId, name: 'Test relay', server_time: this.clock.wall,
            limits: { message_retention: 86400, channel_timeline_retention: 86400, group_message_retention: 86400, max_group_members: 100, max_group_invite_ttl: 86400 } });
        if (method === 'auth.challenge') return response({ nonce: 'test-challenge', created_at: this.clock.wall, expires_at: this.clock.wall + 300 });
        if (method === 'auth.account.verify') {
            const proof = sdk.accountAuthenticationCodec.decode(body); const account = sdk.getAccountId('neo:860833102', proof.accountPublicKey);
            if (!sdk.verifyAccount(sdk.accountAuthenticationInput(proof, account, this.descriptor.relayId, url, context), proof.accountSignature, proof.accountPublicKey)) throw new Error('Invalid fixture account proof');
            return response({ token: `account/${account}`, mode: 'account', expires_at: this.clock.wall + 3600 });
        }
        if (method === 'auth.device.verify') {
            const proof = sdk.deviceAuthenticationCodec.decode(body); sdk.validateCertificate(proof.signerCertificate, context);
            if (!sdk.verifyDevice(sdk.deviceAuthenticationInput(proof, this.descriptor.relayId, url, context), proof.deviceSignature, proof.signerCertificate.signingPublicKey)) throw new Error('Invalid fixture device proof');
            return response({ token: `device/${proof.signerCertificate.account}/${sdk.certificateId(proof.signerCertificate, context)}`, mode: 'device', expires_at: this.clock.wall + 3600 });
        }
        if (method === 'account.route.publish') {
            let route = sdk.accountRouteCodec.decode(body); sdk.validateRoute(route, context, this.clock.wall);
            route = { ...route, relaySignature: sdk.signAccount(sdk.routeRelayInput(route, context), relayPrivateKey) }; this.routes.set(route.account, route); return response(sdk.accountRouteCodec.encode(route));
        }
        if (method === 'account.route.resolve') { const route = this.routes.get(body.account as string); return route ? response(sdk.accountRouteCodec.encode(route)) : absent(); }
        if (method === 'device.state.publish') { const state = sdk.accountDeviceStateCodec.decode(body); sdk.validateDeviceState(state, context); this.states.set(state.account, state); return response({ status: 'accepted' }); }
        if (method === 'device.state.resolve') {
            if (this.resolveFailure === body.account) throw new Error('Device state lookup unavailable');
            if (init.method === 'POST') sdk.validateDeviceStateQuery(sdk.signedDeviceStateQueryCodec.decode(body), context, this.clock.wall);
            const state = this.states.get(body.account as string); return state ? response(sdk.accountDeviceStateCodec.encode(state)) : absent();
        }
        const [, caller, deviceId] = init.headers['X-Meshline-Session']!.split('/');
        const handled = await this.handleRequest?.(method, body, caller!, deviceId!); if (handled) return handled;
        if (method === 'message.send') {
            const request = sdk.messageSendRequestCodec.decode(body); sdk.validateMessageSendRequest(request, this.clock.wall); const envelope = request.envelope;
            if (envelope.from !== caller || envelope.fromDeviceId !== deviceId) throw new Error('Message session identity differs');
            const certificate = this.states.get(caller!)!.certificates.find(value => sdk.certificateId(value, context) === deviceId)!;
            sdk.verifyMessageEnvelope(envelope, certificate, context); await this.beforeSend?.(request); this.submissions.push(request);
            const key = `${caller}|${envelope.messageId}`; let delivery = this.deliveries.get(key);
            if (delivery && sdk.messageSendRequestCodec.stringify(delivery.request) !== sdk.messageSendRequestCodec.stringify(request)) throw new Error('Non-identical retry');
            if (!delivery) {
                delivery = { request, certificate, acceptedAt: this.clock.wall }; this.deliveries.set(key, delivery); this.#append(envelope.to, delivery, false);
                if (envelope.to !== envelope.from && request.senderBoxes) this.#append(envelope.from, delivery, true);
            }
            if (this.loseResponse) throw new Error('Relay accepted message but response was lost');
            return response({ status: this.deliveryStatus, accepted_at: delivery.acceptedAt });
        }
        if (method === 'message.delivery.status') { const delivery = this.deliveries.get(`${caller}|${body.message_id}`); return delivery ? response({ status: this.deliveryStatus, accepted_at: delivery.acceptedAt }) : absent(); }
        if (method === 'message.timeline.sync') {
            const after = Number(body.after ?? -1); const certificates = new Map<string, sdk.DeviceCertificate>(); const entries: sdk.MessageTimelineEntry[] = [];
            for (const item of this.timelines.get(caller!) ?? []) {
                if (item.sequence <= after) continue; const box = (item.sender ? item.delivery.request.senderBoxes : item.delivery.request.recipientBoxes)?.find(value => value.deviceId === deviceId); if (!box) continue;
                const envelope = item.delivery.request.envelope; certificates.set(envelope.fromDeviceId, item.delivery.certificate);
                entries.push({ sequence: item.sequence, envelope, keyBox: box, acceptedAt: item.delivery.acceptedAt });
            }
            return response(sdk.messageTimelinePageCodec.encode({ items: entries, certificates: [...certificates.values()], hasMore: false }));
        }
        throw new Error(`Unexpected fixture method ${method}`);
    };
    #append(account: string, delivery: Delivery, sender: boolean): void { const items = this.timelines.get(account) ?? []; items.push({ delivery, sender, sequence: items.length }); this.timelines.set(account, items); }
    async client(seed: number, path?: string) {
        if (!path) { const directory = await mkdtemp(join(tmpdir(), 'meshline-message-manager-')); this.directories.push(directory); path = join(directory, 'state.sqlite'); }
        const store = new NodeSqliteStore(path); this.resources.push(store); await store.migrate();
        const key = new Uint8Array(32).fill(seed); const accountId = sdk.getAccountId('neo:860833102', sdk.accountPublicKey(key));
        const signer = { accountId, publicKey: sdk.accountPublicKey(key), sign: async (input: Uint8Array) => sdk.signAccount(input, key) };
        const protector: sdk.SecretProtector = {
            protect: async (plaintext, purpose) => { const nonce = sdk.systemRandom.bytes(12); return sdk.concatBytes(nonce, sdk.encryptAes(key, nonce, plaintext, sdk.encodeUtf8(purpose))); },
            unprotect: async (ciphertext, purpose) => sdk.decryptAes(key, ciphertext.slice(0, 12), ciphertext.slice(12), sdk.encodeUtf8(purpose)),
        };
        const pool = new sdk.RelayClientPool({ context, accountId, clock: this.clock, fetch: this.fetch }, new TestRegistry()); this.resources.push(pool);
        const account = new sdk.AccountManager({ context, accountId, store, relayClients: pool, accountSigner: signer, clock: this.clock }); this.resources.push(account); await account.initialize();
        const device = new sdk.DeviceManager({ context, accountId, store, relayClients: pool, accountManager: account, accountSigner: signer, clock: this.clock, secretProtector: protector }); this.resources.push(device); await device.initialize();
        if (!device.local) {
            const existing = this.states.get(accountId); if (existing) { await account.getRoute(); await device.getOwnDeviceState(this.descriptor.relayId); }
            const certificate = await device.createDevice(3600); if (!account.route) await account.publishRoute(this.descriptor.relayId, { validitySeconds: 3600 });
            await device.publishDeviceState(this.descriptor.relayId, { certificates: [...existing?.certificates ?? [], certificate] });
        }
        const messages = new sdk.MessageManager({ context, accountId, store, relayClients: pool, accountManager: account, deviceManager: device, clock: this.clock, secretProtector: protector }); this.resources.push(messages); await messages.initialize();
        return { accountId, path, store, pool, account, device, messages, protector, async dispose() { await messages.dispose(); await device.dispose(); await account.dispose(); await pool.dispose(); await store.dispose(); } };
    }
    async until(condition: () => Promise<boolean>): Promise<void> {
        for (let count = 0; count < 50; count++) { if (await condition()) return; this.clock.tick(); await new Promise(resolve => setTimeout(resolve, 15)); }
        throw new Error('Expected messaging condition was not reached');
    }
    async dispose(): Promise<void> { for (const resource of this.resources.reverse()) await resource.dispose(); for (const directory of this.directories) await removeTestDirectory(directory); }
}
