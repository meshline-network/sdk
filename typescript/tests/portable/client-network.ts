import * as sdk from '@meshline/sdk';
import { context } from './relay-fixture.js';
import { MessagingClock } from './clock.js';
const peerId = 'QmNQa1FSTXNHmrjjfgUW3Px3Vkke4oKiFWdigWkYSux2Pi';

interface Relay { key: Uint8Array; descriptor: sdk.RelayDescriptor; states: Map<string, sdk.AccountDeviceState>; staged: Map<string, sdk.AccountDeviceState>; profiles: Map<string, sdk.ProfileResolveResult> }
export interface ClientNetworkOptions { readonly random: sdk.RandomSource; createStore(path?: string): Promise<{ store: sdk.MeshlineStore; path: string }> }
export class PortableClientNetwork {
    readonly clock = new MessagingClock(); readonly relays: Relay[]; readonly routes = new Map<string, sdk.AccountRoute>();
    readonly requests: { relay: string; method: string; body: sdk.JsonObject }[] = []; readonly resources: { dispose(): Promise<void> }[] = [];
    loseAfter: string | undefined; failBefore: string | undefined; unavailableRelay: string | undefined; stageSeconds = 300; acceptedInitial = false;
    beforeRoute: (() => Promise<void>) | undefined;
    constructor(readonly options: ClientNetworkOptions) {
        this.relays = [7, 8, 9].map((seed, index) => {
            const key = new Uint8Array(32).fill(seed); const publicKey = sdk.accountPublicKey(key); const relayId = sdk.getRelayId(publicKey); const host = `https://client-${index}.example`;
            let descriptor: sdk.RelayDescriptor = { relayId, publicKey, endpoints: [host + '/v1', `/dns4/client-${index}.example/tcp/4201/p2p/${peerId}`], capabilities: ['channel.host.v1', 'group.host.v1'], expiresAt: 2000000000, relaySignature: new Uint8Array(64) };
            descriptor = { ...descriptor, relaySignature: sdk.signAccount(sdk.relayDescriptorInput(descriptor, context), key, options.random) };
            return { key, descriptor, states: new Map(), staged: new Map(), profiles: new Map() };
        });
    }
    readonly registry: sdk.RelayRegistry = {
        context, getRelay: async relayId => { const relay = this.relays.find(value => value.descriptor.relayId === relayId); return relay && this.#entry(relay); },
        getRelays: () => { const self = this; return (async function* () { for (const relay of self.relays) yield self.#entry(relay); })(); },
    };
    #entry(relay: Relay): sdk.RelayEntry { return { relayId: relay.descriptor.relayId, endpoint: relay.descriptor.endpoints[0]!, status: 'active', updatedAt: 1730000000000n }; }
    readonly fetch: sdk.RelayFetch = async (url, init) => {
        const address = new URL(url); const relay = this.relays.find(value => new URL(value.descriptor.endpoints[0]!).origin === address.origin)!;
        if (!relay) throw new Error('Unknown client test relay'); const relayId = relay.descriptor.relayId;
        if (this.unavailableRelay === relayId) throw new TypeError('Relay unavailable');
        const method = address.pathname.replace(/^\/v1\/?/, '').replaceAll('/', '.'); const body = init.body ? sdk.requireObject(sdk.parseJson(init.body)) : Object.fromEntries(address.searchParams) as sdk.JsonObject;
        const response = (value?: unknown, status = value === undefined ? 204 : 200) => new Response(value === undefined ? null : JSON.stringify(value), { status, ...(value === undefined ? {} : { headers: { 'content-type': 'application/json' } }) });
        const absent = () => response({ code: 'not_found', message: 'Not found' }, 404); this.requests.push({ relay: relayId, method, body });
        if (this.failBefore === method) throw new TypeError('Test relay unavailable before request acceptance');
        const accepted = (value?: unknown) => { if (this.loseAfter === method) throw new TypeError('Test response lost after acceptance'); return response(value); };
        if (method === 'relay.descriptor') return response(sdk.relayDescriptorCodec.encode(relay.descriptor));
        if (method === 'auth.challenge') return response({ nonce: 'client-test', created_at: this.clock.wall, expires_at: this.clock.wall + 300 });
        if (method === 'auth.account.verify') {
            const proof = sdk.accountAuthenticationCodec.decode(body); const account = sdk.getAccountId('neo:860833102', proof.accountPublicKey);
            if (!sdk.verifyAccount(sdk.accountAuthenticationInput(proof, account, relayId, url, context), proof.accountSignature, proof.accountPublicKey)) throw new Error('Invalid account authentication');
            return response({ token: `account/${account}`, mode: 'account', expires_at: this.clock.wall + 3600 });
        }
        if (method === 'auth.device.verify') {
            const proof = sdk.deviceAuthenticationCodec.decode(body); sdk.validateCertificate(proof.signerCertificate, context); const id = sdk.certificateId(proof.signerCertificate, context);
            sdk.authorizedDevice(relay.states.get(proof.signerCertificate.account)!, id, context, this.clock.wall);
            if (!sdk.verifyDevice(sdk.deviceAuthenticationInput(proof, relayId, url, context), proof.deviceSignature, proof.signerCertificate.signingPublicKey)) throw new Error('Invalid device authentication');
            return response({ token: `device/${proof.signerCertificate.account}/${id}`, mode: 'device', expires_at: this.clock.wall + 3600 });
        }
        if (method === 'account.route.resolve') { const route = this.routes.get(String(body.account)); return route ? response(sdk.accountRouteCodec.encode(route)) : absent(); }
        if (method === 'account.route.publish') {
            await this.beforeRoute?.(); sdk.throwIfAborted(init.signal); const proposed = sdk.accountRouteCodec.decode(body); sdk.validateRoute(proposed, context, this.clock.wall);
            if (proposed.relayId !== relayId) throw new Error('Wrong route receiver'); const previous = this.routes.get(proposed.account);
            if (previous && ['older', 'conflict'].includes(sdk.compareRoutes(proposed, previous))) return response({ code: 'stale_state', message: 'Stale route' }, 409);
            const route = { ...proposed, relaySignature: sdk.signAccount(sdk.routeRelayInput(proposed, context), relay.key, this.options.random) }; this.routes.set(route.account, route);
            const staged = relay.staged.get(route.account); if (staged) { relay.states.set(route.account, staged); relay.staged.delete(route.account); }
            return accepted(sdk.accountRouteCodec.encode(route));
        }
        if (method === 'device.state.publish') {
            const state = sdk.accountDeviceStateCodec.decode(body); sdk.validateDeviceState(state, context);
            const acceptedHere = this.routes.get(state.account)?.relayId === relayId || this.acceptedInitial;
            const previous = (acceptedHere ? relay.states : relay.staged).get(state.account);
            if (previous && (previous.revision > state.revision || previous.revision === state.revision && sdk.accountDeviceStateCodec.stringify(previous) !== sdk.accountDeviceStateCodec.stringify(state))) return response({ code: 'stale_state', message: 'Stale devices' }, 409);
            if (acceptedHere) { relay.states.set(state.account, state); return accepted({ status: 'accepted' }); }
            relay.staged.set(state.account, state); return accepted({ status: 'staged', staged_until: this.clock.wall + this.stageSeconds });
        }
        if (method === 'device.state.resolve') { const value = relay.states.get(String(body.account)); return value ? response(sdk.accountDeviceStateCodec.encode(value)) : absent(); }
        if (method === 'profile.resolve') { const value = relay.profiles.get(String(body.account)); return value ? response(sdk.profileResolveResultCodec.encode(value)) : absent(); }
        if (method === 'profile.publish') {
            const profile = sdk.accountProfileCodec.decode(body); const id = init.headers['X-Meshline-Session']!.split('/')[2]!; const signerCertificate = sdk.authorizedDevice(relay.states.get(profile.account)!, id, context, this.clock.wall);
            const value = { profile, signerCertificate }; sdk.validateProfileResult(value, context, profile.account); relay.profiles.set(profile.account, value); return accepted();
        }
        if (method === 'message.timeline.sync') return response({ items: [], certificates: [], has_more: false });
        if (method === 'message.send') return response({ status: 'target_accepted', accepted_at: this.clock.wall });
        if (method === 'message.delivery.status') return absent();
        throw new Error(`Unexpected client fixture method ${method}`);
    };
    async open(seed = 91, path?: string, withSigner = true) {
        const created = await this.options.createStore(path); const store = created.store; path = created.path; this.resources.push(store); await store.migrate();
        const key = new Uint8Array(32).fill(seed); const publicKey = sdk.accountPublicKey(key); const accountId = sdk.getAccountId('neo:860833102', publicKey);
        const signer: sdk.AccountSigner = { accountId, publicKey, sign: async input => sdk.signAccount(input, key, this.options.random) };
        const protector: sdk.SecretProtector = { protect: async (bytes, purpose) => { const nonce = this.options.random.bytes(12); return sdk.concatBytes(nonce, sdk.encryptAes(key, nonce, bytes, sdk.encodeUtf8(purpose))); }, unprotect: async (bytes, purpose) => sdk.decryptAes(key, bytes.slice(0, 12), bytes.slice(12), sdk.encodeUtf8(purpose)) };
        const pool = new sdk.RelayClientPool({ context, accountId, clock: this.clock, random: this.options.random, fetch: this.fetch }, this.registry); this.resources.push(pool);
        const client = new sdk.MeshlineClient({ context, accountId, store, relayClients: pool, secretProtector: protector, ...(withSigner ? { accountSigner: signer } : {}), clock: this.clock, random: this.options.random }); this.resources.push(client); await client.initialize();
        return { client, store, pool, signer, path, async dispose() { await client.dispose(); await pool.dispose(); await store.dispose(); } };
    }
    async dispose(): Promise<void> { for (const resource of this.resources.reverse()) await resource.dispose(); }
}
