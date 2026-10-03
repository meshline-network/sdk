import { systemRandom, verifyDevice, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import { createIdentifier, deriveResourceId, validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId, validateRelayId } from '../identity/neo.js';
import {
    channelCloseRequestCodec, channelDescriptorCodec, channelPayloadCodec, channelPayloadInput, channelPostCodec, channelPostEditCodec, channelReadPageCodec,
    channelReadQueryCodec, channelReportRequestCodec, channelResolveQueryCodec, channelResolveResultCodec, channelSubscriptionRequestCodec, channelTimelineChangedCodec,
    validateChannelPayload, validateChannelPostRef, validateChannelReadQuery, validateChannelRef, validateChannelReportRequest, validateChannelTimelineChanged, verifyChannelDescriptor,
    type ChannelDescriptor, type ChannelPayload, type ChannelPost, type ChannelPostRef, type ChannelReadQuery, type ChannelRef, type ChannelResolveResult,
} from '../models/channels.js';
import type { ContentReference, MessageBody } from '../models/content.js';
import { certificateId } from '../models/identity.js';
import { ChannelRepository, type ChannelChanges, type ChannelInfo, type ChannelOperation, type ChannelPostChange, type ChannelPostInfo, type ChannelReader } from '../channels/repository.js';
import { snapshotReader } from '../messages/repository.js';
import { nextRevision } from '../protocol/context.js';
import { canonicalJson, requireObject, requireSafeInteger } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { AsyncPulse } from '../runtime/async-pulse.js';
import { throwIfAborted } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import type { MeshlineStore, QueryReader } from '../storage/store.js';
import type { RelayClient } from '../transport/client.js';
import type { HttpMethod } from '../transport/http.js';
import type { RelayClientPool } from '../transport/pool.js';
import { RelayError } from '../transport/relay-error.js';
import type { RelaySubscription } from '../transport/subscription.js';
import type { DeviceManager } from './device.js';
import { ClientComponent, type ClientOptions } from './component.js';

export interface ChannelManagerOptions extends ClientOptions { readonly store: MeshlineStore; readonly relayClients: RelayClientPool; readonly deviceManager: DeviceManager; readonly random?: RandomSource }
export interface ChannelUpdate { readonly name?: string; readonly description?: string | null; readonly moderators?: readonly string[] | null }
export interface ChannelPostDraft { readonly body?: MessageBody; readonly attachments?: readonly ContentReference[] }
export interface ChannelPostUpdate { readonly body?: MessageBody | null; readonly attachments?: readonly ContentReference[] | null }
export interface ChannelHistoryRequest { readonly cursor?: string; readonly limit?: number }
export interface ChannelHistoryPage { readonly items: readonly ChannelPostInfo[]; readonly nextCursor?: string }
export interface ChannelEvents {
    readonly channelChanged: ChannelInfo;
    readonly timelineChanged: { readonly channel: ChannelRef; readonly changes: readonly ChannelPostChange[] };
    readonly followChanged: { readonly channel: ChannelRef; readonly isFollowed: boolean };
}
interface Subscription { client: RelayClient; subscription: RelaySubscription; selected: Set<string>; detach: (() => void)[] }

/** Public channels with verified historical permissions, durable exact requests and explicit runtime ownership. */
export class ChannelManager extends ClientComponent {
    readonly #store: MeshlineStore; readonly #pool: RelayClientPool; readonly #device: DeviceManager; readonly #random: RandomSource;
    readonly #repository: ChannelRepository; readonly #writes = new AsyncGate(); readonly #events = new EventHub<ChannelEvents>();
    readonly #subscriptions = new Map<string, Subscription>(); #pulse: AsyncPulse | undefined; #jobs: Promise<void>[] = [];
    constructor(options: ChannelManagerOptions) {
        super(options); for (const dependency of [options.relayClients, options.deviceManager]) if (dependency.accountId !== this.accountId || dependency.context.toString() !== this.context.toString()) throw new ProtocolError('invalid_context', 'Channel dependencies belong to another network or account.');
        this.#store = options.store; this.#pool = options.relayClients; this.#device = options.deviceManager; this.#random = options.random ?? systemRandom;
        this.#repository = new ChannelRepository(this.#store, this.context, this.accountId);
    }
    on<K extends keyof ChannelEvents>(event: K, listener: EventListener<ChannelEvents[K]>): () => void { return this.#events.on(event, listener); }
    #notify<K extends keyof ChannelEvents>(event: K, value: ChannelEvents[K]): void { this.#events.notify(event, value, error => this.notifyBackgroundError({ operation: 'observer', resource: event, error })); }
    #changed(channel: ChannelRef, changes: ChannelChanges): void { if (changes.channel) this.#notify('channelChanged', changes.channel); if (changes.posts.length) this.#notify('timelineChanged', { channel, changes: changes.posts }); }
    protected override async onInitialize(signal: AbortSignal): Promise<void> { this.#device.ensureInitialized(); await this.#store.initialize({ context: this.context.toString(), accountId: this.accountId }, signal); }
    async #hosting(relayId: string, signal: AbortSignal): Promise<RelayClient> {
        validateRelayId(relayId); const relay = await this.#pool.get(relayId, { mode: 'device', signer: this.#device }, signal);
        if (!(await relay.getDescriptor(signal)).capabilities?.includes('channel.host.v1')) throw new ProtocolError('unsupported_capability', 'Relay does not support channel hosting.'); return relay;
    }
    async #resolve(relay: RelayClient, channel: ChannelRef, revision: number | undefined, signal: AbortSignal): Promise<ChannelResolveResult> {
        const result = channelResolveResultCodec.decode((await relay.requestHttp('GET', 'channel.resolve', channelResolveQueryCodec.encode({ channelId: channel.channelId, ...(revision === undefined ? {} : { revision }) }), { signal }))!);
        verifyChannelDescriptor(result, this.context, channel); if (revision !== undefined && result.descriptor.revision !== revision) throw new ProtocolError('invalid_revision', 'Relay returned another descriptor revision.'); return result;
    }
    #reader(relay: RelayClient, channel: ChannelRef, signal: AbortSignal): ChannelReader { return {
        read: async query => channelReadPageCodec.decode((await relay.requestHttp('GET', 'channel.read', channelReadQueryCodec.encode(query), { signal }))!),
        resolve: revision => this.#resolve(relay, channel, revision, signal),
    }; }
    async #sign<T extends ChannelPayload>(payload: T, signal: AbortSignal): Promise<T> {
        const copy = channelPayloadCodec.decode(channelPayloadCodec.encode(payload)); validateChannelPayload(copy, this.context);
        const certificate = this.#device.certificate; const id = certificateId(certificate, this.context); const input = channelPayloadInput(copy, this.context);
        const signature = await this.#device.sign(input, signal);
        if (id !== certificateId(this.#device.certificate, this.context) || !verifyDevice(input, signature, certificate.signingPublicKey)) throw new StateConflictError('Local channel signing identity changed or returned an invalid signature.');
        return { ...copy, value: { ...copy.value, deviceSignature: signature } } as T;
    }
    #canWrite(descriptor: ChannelDescriptor, ownerOnly = false): void {
        if (descriptor.status !== 'active') throw new ProtocolError('closed_channel', 'Channel is closed.');
        if (descriptor.creator !== this.accountId && (ownerOnly || !descriptor.moderators?.includes(this.accountId))) throw new ProtocolError('forbidden', 'Account is not permitted to modify this channel.');
    }
    async #saveOperation(operation: ChannelOperation, signal: AbortSignal): Promise<void> { try { await this.#repository.saveOperation(operation, signal); } finally { this.#pulse?.pulse(); } }
    async #send(relay: RelayClient, operation: ChannelOperation, recovering: boolean, signal: AbortSignal): Promise<number | undefined> {
        const methods: Record<ChannelOperation['method'], HttpMethod> = { 'channel.create': 'POST', 'channel.update': 'PUT', 'channel.close': 'DELETE', 'channel.post': 'PUT', 'channel.post.edit': 'PATCH', 'channel.post.delete': 'DELETE' };
        const value = operation.payload; const request = operation.method === 'channel.close' && value.kind === 'descriptor'
            ? channelCloseRequestCodec.encode({ channelId: value.value.channelId, revision: value.value.revision, updatedAt: value.value.updatedAt, deviceSignature: value.value.deviceSignature }) : requireObject(channelPayloadCodec.encode(value));
        try {
            const result = await relay.requestHttp(methods[operation.method], operation.method, request, { signal });
            if (operation.method === 'channel.post') { const sequence = requireObject(result!).sequence; requireSafeInteger(sequence, 1); return sequence; }
            return undefined;
        } catch (error) { if (!recovering && error instanceof RelayError && error.isDefinitiveRejection) await this.#repository.completeOperation(operation); throw error; }
    }
    async #publishDescriptor(relay: RelayClient, operation: ChannelOperation, recovering: boolean, signal: AbortSignal): Promise<void> {
        if (operation.payload.kind !== 'descriptor') throw new ProtocolError('invalid_operation', 'Expected a descriptor operation.');
        const descriptor = operation.payload.value; let accepted: ChannelResolveResult | undefined;
        try { accepted = await this.#resolve(relay, operation.channel, descriptor.revision, signal); }
        catch (error) { if (!(error instanceof RelayError && error.code === 'not_found')) throw error; }
        if (accepted && channelDescriptorCodec.stringify(accepted.descriptor) !== channelDescriptorCodec.stringify(descriptor)) {
            const changed = await this.#repository.saveDescriptor(operation.channel, accepted, operation); if (changed) this.#notify('channelChanged', changed);
            throw new StateConflictError('Channel revision is occupied by a different descriptor.');
        }
        if (!accepted) { await this.#send(relay, operation, recovering, signal); accepted = { descriptor, signerCertificate: this.#device.certificate }; }
        const changed = await this.#repository.saveDescriptor(operation.channel, accepted, operation); if (changed) this.#notify('channelChanged', changed); this.#pulse?.pulse();
    }
    async #synchronize(relay: RelayClient, channel: ChannelRef, after: number, advance: boolean, signal: AbortSignal): Promise<void> {
        for (;;) { const result = await this.#repository.readPage(channel, { channelId: channel.channelId, after }, this.#reader(relay, channel, signal), advance, signal); this.#changed(channel, result.changes);
            if (!result.page.hasMore) return; after = result.page.events.at(-1)!.sequence; }
    }
    async #publishPost(relay: RelayClient, operation: ChannelOperation, recovering: boolean, signal: AbortSignal): Promise<ChannelPostRef> {
        if (operation.payload.kind !== 'post') throw new ProtocolError('invalid_operation', 'Expected a post publication.'); const request = operation.payload.value;
        let sequence = operation.acceptedSequence ?? await this.#repository.publication(operation.channel, request, signal);
        if (sequence === undefined) { sequence = (await this.#send(relay, operation, recovering, signal))!; operation = await this.#repository.acknowledgePublication(operation, sequence); }
        await this.#synchronize(relay, operation.channel, sequence - 1, false, signal); await this.#repository.confirmPublication(operation.channel, sequence, request, signal);
        await this.#repository.completeOperation(operation); this.#pulse?.pulse(); return { channel: operation.channel, sequence };
    }
    async #mutate(relay: RelayClient, operation: ChannelOperation, recovering: boolean, signal: AbortSignal): Promise<void> {
        if (operation.payload.kind !== 'edit' && operation.payload.kind !== 'delete') throw new ProtocolError('invalid_operation', 'Expected a post edit or deletion.');
        await this.#send(relay, operation, recovering, signal); const change = await this.#repository.completeOperation(operation, operation.payload.kind === 'delete');
        if (change) this.#changed(operation.channel, { posts: [change] }); this.#pulse?.pulse();
        if (!recovering) await this.#synchronize(relay, operation.channel, operation.payload.value.targetSequence - 1, false, signal);
    }
    createChannel(relayId: string, name: string, options: { readonly description?: string; readonly moderators?: readonly string[]; readonly signal?: AbortSignal } = {}): Promise<ChannelInfo> {
        validateRelayId(relayId); const description = options.description; const moderators = options.moderators && [...options.moderators];
        return this.runOperation(scope => this.#writes.run(async () => {
            const relay = await this.#hosting(relayId, scope); const nonce = this.#random.bytes(16); const now = this.clock.nowSeconds();
            const payload = await this.#sign({ kind: 'descriptor', value: { channelId: deriveResourceId('channel', this.accountId, relayId, nonce, this.context), nonce, creator: this.accountId, relayId,
                name, ...(description === undefined ? {} : { description }), ...(moderators === undefined ? {} : { moderators }), revision: 0, status: 'active', createdAt: now, updatedAt: now, deviceSignature: new Uint8Array(64) } }, scope);
            const channel = { channelId: payload.value.channelId, relayId }; const operation: ChannelOperation = { channel, method: 'channel.create', payload };
            await this.#saveOperation(operation, scope); await this.#publishDescriptor(relay, operation, false, scope); return this.#info(channel, scope);
        }, scope), options.signal);
    }
    async #info(channel: ChannelRef, signal: AbortSignal): Promise<ChannelInfo> { const record = await this.#repository.get(channel, signal); return { ref: channel, isFollowed: record.isFollowed, ...(record.descriptor ? { descriptor: record.descriptor } : {}) }; }
    getChannel(channel: ChannelRef, signal?: AbortSignal): Promise<ChannelInfo> {
        channel = { ...channel }; validateChannelRef(channel);
        return this.runOperation(async scope => { const relay = await this.#hosting(channel.relayId, scope); const changed = await this.#repository.saveDescriptor(channel, await this.#resolve(relay, channel, undefined, scope), undefined, scope);
            if (changed) this.#notify('channelChanged', changed); return this.#info(channel, scope); }, signal);
    }
    updateChannel(channel: ChannelRef, update: ChannelUpdate, signal?: AbortSignal): Promise<ChannelInfo> {
        channel = { ...channel }; validateChannelRef(channel); const copy = { ...update, ...(Array.isArray(update.moderators) ? { moderators: [...update.moderators] } : {}) };
        if (copy.name === null) throw new TypeError('Channel name cannot be deleted.');
        return this.runOperation(scope => this.#writes.run(async () => {
            const relay = await this.#hosting(channel.relayId, scope); const current = (await this.#resolve(relay, channel, undefined, scope)).descriptor; this.#canWrite(current, true);
            const { description, moderators, ...base } = current; const nextDescription = copy.description === undefined ? description : copy.description; const nextModerators = copy.moderators === undefined ? moderators : copy.moderators;
            const payload = await this.#sign({ kind: 'descriptor', value: { ...base, name: copy.name ?? current.name, ...(nextDescription == null ? {} : { description: nextDescription }), ...(nextModerators == null ? {} : { moderators: nextModerators }), revision: nextRevision(current.revision), updatedAt: this.clock.nowSeconds() } }, scope);
            const operation: ChannelOperation = { channel, method: 'channel.update', payload }; await this.#saveOperation(operation, scope); await this.#publishDescriptor(relay, operation, false, scope); return this.#info(channel, scope);
        }, scope), signal);
    }
    closeChannel(channel: ChannelRef, signal?: AbortSignal): Promise<void> {
        channel = { ...channel }; validateChannelRef(channel);
        return this.runOperation(scope => this.#writes.run(async () => { const relay = await this.#hosting(channel.relayId, scope); const current = (await this.#resolve(relay, channel, undefined, scope)).descriptor; this.#canWrite(current, true);
            const payload = await this.#sign({ kind: 'descriptor', value: { ...current, status: 'closed', revision: nextRevision(current.revision), updatedAt: this.clock.nowSeconds() } }, scope);
            const operation: ChannelOperation = { channel, method: 'channel.close', payload }; await this.#saveOperation(operation, scope); await this.#publishDescriptor(relay, operation, false, scope);
        }, scope), signal);
    }
    publishPost(channel: ChannelRef, draft: ChannelPostDraft, signal?: AbortSignal): Promise<ChannelPostInfo> {
        channel = { ...channel }; validateChannelRef(channel); const basis = channelPostCodec.decode(channelPostCodec.encode({ ...draft, channelId: channel.channelId, messageId: createIdentifier('message', this.#random), deviceSignature: new Uint8Array(64) }));
        return this.runOperation(scope => this.#writes.run(async () => { const relay = await this.#hosting(channel.relayId, scope); this.#canWrite((await this.#resolve(relay, channel, undefined, scope)).descriptor);
            const payload = await this.#sign({ kind: 'post', value: basis }, scope); const operation: ChannelOperation = { channel, method: 'channel.post', payload }; await this.#saveOperation(operation, scope);
            const reference = await this.#publishPost(relay, operation, false, scope); const result = await this.#repository.post(reference, scope); if (!result) throw new ProtocolError('post_deleted', 'The accepted post was already deleted.'); return result;
        }, scope), signal);
    }
    editPost(post: ChannelPostRef, update: ChannelPostUpdate, signal?: AbortSignal): Promise<ChannelPostInfo> {
        post = { channel: { ...post.channel }, sequence: post.sequence }; validateChannelPostRef(post);
        const basis = channelPostEditCodec.decode(channelPostEditCodec.encode({ ...update, channelId: post.channel.channelId, targetSequence: post.sequence, deviceSignature: new Uint8Array(64) }));
        return this.runOperation(scope => this.#writes.run(async () => { const relay = await this.#hosting(post.channel.relayId, scope); this.#canWrite((await this.#resolve(relay, post.channel, undefined, scope)).descriptor);
            const payload = await this.#sign({ kind: 'edit', value: basis }, scope); const operation: ChannelOperation = { channel: post.channel, method: 'channel.post.edit', payload }; await this.#saveOperation(operation, scope); await this.#mutate(relay, operation, false, scope);
            const result = await this.#repository.post(post, scope); if (!result) throw new ProtocolError('post_unavailable', 'The edited post is no longer available.'); return result;
        }, scope), signal);
    }
    deletePost(post: ChannelPostRef, signal?: AbortSignal): Promise<void> {
        post = { channel: { ...post.channel }, sequence: post.sequence }; validateChannelPostRef(post);
        return this.runOperation(scope => this.#writes.run(async () => { const relay = await this.#hosting(post.channel.relayId, scope); this.#canWrite((await this.#resolve(relay, post.channel, undefined, scope)).descriptor);
            const payload = await this.#sign({ kind: 'delete', value: { channelId: post.channel.channelId, targetSequence: post.sequence, deviceSignature: new Uint8Array(64) } }, scope);
            const operation: ChannelOperation = { channel: post.channel, method: 'channel.post.delete', payload }; await this.#saveOperation(operation, scope); await this.#mutate(relay, operation, false, scope);
        }, scope), signal);
    }
    reportPost(post: ChannelPostRef, reason: string, signal?: AbortSignal): Promise<void> {
        post = { channel: { ...post.channel }, sequence: post.sequence }; validateChannelPostRef(post); const request = { channelId: post.channel.channelId, targetSequence: post.sequence, reason }; validateChannelReportRequest(request);
        return this.runOperation(async scope => { const relay = await this.#hosting(post.channel.relayId, scope); await relay.requestHttp('PUT', 'channel.post.report', channelReportRequestCodec.encode(request), { signal: scope }); }, signal);
    }
    loadChannelHistory(channel: ChannelRef, page: ChannelHistoryRequest = {}, signal?: AbortSignal): Promise<ChannelHistoryPage> {
        channel = { ...channel }; validateChannelRef(channel); let before: number | undefined;
        if (page.cursor !== undefined) { if (!/^(0|[1-9][0-9]*)$/.test(page.cursor)) throw new TypeError('History cursor must be a nonnegative safe integer.'); before = Number(page.cursor); requireSafeInteger(before, 0); }
        const query: ChannelReadQuery = { channelId: channel.channelId, ...(before === undefined ? {} : { before }), ...(page.limit === undefined ? {} : { limit: page.limit }) }; validateChannelReadQuery(query);
        return this.runOperation(scope => this.#writes.run(async () => { const relay = await this.#hosting(channel.relayId, scope); const result = await this.#repository.readPage(channel, query, this.#reader(relay, channel, scope), false, scope); this.#changed(channel, result.changes);
            const items: ChannelPostInfo[] = []; for (const event of result.page.events) if (event.payload.kind === 'post') { const post = await this.#repository.post({ channel, sequence: event.sequence }, scope); if (post) items.push(post); }
            return { items, ...(result.page.hasMore ? { nextCursor: String(result.page.events[0]!.sequence) } : {}) };
        }, scope), signal);
    }
    getPosts(query: { readonly channelId?: string; readonly author?: string } = {}, signal?: AbortSignal): Promise<QueryReader<ChannelPostInfo>> {
        const { channelId, author } = query; if (channelId !== undefined) validateIdentifier('channel', channelId); if (author !== undefined) validateAccountId(author); return this.runOperation(scope => this.#repository.posts(channelId, author, scope), signal);
    }
    async follow(channel: ChannelRef, signal?: AbortSignal): Promise<void> {
        channel = { ...channel }; validateChannelRef(channel); await this.getChannel(channel, signal);
        const changed = await this.runOperation(scope => this.#repository.follow(channel, true, scope), signal); if (changed) this.#notify('followChanged', { channel, isFollowed: true }); this.#pulse?.pulse();
    }
    async unfollow(channel: ChannelRef, signal?: AbortSignal): Promise<void> {
        channel = { ...channel }; validateChannelRef(channel); const changed = await this.runOperation(scope => this.#repository.follow(channel, false, scope), signal); if (changed) this.#notify('followChanged', { channel, isFollowed: false }); this.#pulse?.pulse();
    }
    getFollowed(relayId?: string, signal?: AbortSignal): Promise<QueryReader<ChannelInfo>> { if (relayId !== undefined) validateRelayId(relayId); return this.runOperation(async scope => snapshotReader((await this.#repository.followed(relayId, scope)).map(record => ({ ref: record.channel, isFollowed: record.isFollowed, ...(record.descriptor ? { descriptor: record.descriptor } : {}) }))), signal); }
    protected override async onStart(): Promise<void> { void this.#device.certificate; this.#pulse = new AsyncPulse(); this.#jobs = [this.#refresh(this.runtimeSignal), this.#poll(this.runtimeSignal)]; this.#pulse.pulse(); }
    async #poll(signal: AbortSignal): Promise<void> { try { for (;;) { await this.clock.delay(30000, signal); this.#pulse?.pulse(); } } catch (error) { if (!signal.aborted) throw error; } }
    async #recover(signal: AbortSignal): Promise<void> {
        await this.#writes.run(async () => { for (const operation of await this.#repository.operations(signal)) {
            try {
                const relay = await this.#hosting(operation.channel.relayId, signal);
                if (operation.payload.kind === 'descriptor') await this.#publishDescriptor(relay, operation, true, signal);
                else if (operation.payload.kind === 'post') {
                    if (operation.acceptedSequence === undefined) await this.#synchronize(relay, operation.channel, (await this.#repository.get(operation.channel, signal)).syncSequence, false, signal);
                    await this.#publishPost(relay, operation, true, signal);
                } else {
                    await this.#synchronize(relay, operation.channel, operation.payload.value.targetSequence - 1, false, signal);
                    if (await this.#repository.hasOperation(operation, signal)) await this.#mutate(relay, operation, true, signal);
                }
            } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'recover_channel', resource: operation.channel.channelId, error }); }
        } }, signal);
    }
    async #subscribe(client: RelayClient, channelIds: readonly string[], signal: AbortSignal): Promise<void> {
        if (!(await client.getDescriptor(signal)).endpoints.some(endpoint => endpoint.startsWith('wss://'))) return;
        const limit = (await client.getInfo(signal)).limits.maxChannelSubscriptions ?? 0; const selected = channelIds.slice(0, limit);
        let observed = this.#subscriptions.get(client.relayId);
        if (observed?.client !== client) {
            if (observed) { for (const detach of observed.detach) detach(); await observed.subscription.dispose(); }
            const subscription = client.createSubscription('channel.subscribe', { channel_ids: [] }, { signal, onSubscribed: () => { this.#pulse?.pulse(); }, onError: error => this.notifyBackgroundError({ operation: 'subscribe_channel', resource: client.relayId, error }) });
            observed = { client, subscription, selected: new Set(), detach: [] }; this.#subscriptions.set(client.relayId, observed); const owner = observed;
            owner.detach.push(client.on('notificationReceived', notification => { if (notification.method !== 'channel.timeline.changed' || !owner.selected.size) return;
                const value = channelTimelineChangedCodec.decode(notification.params!); validateChannelTimelineChanged(value); if (owner.selected.has(value.channelId)) this.#pulse?.pulse(); }),
                client.on('socketConnected', () => { this.#pulse?.pulse(); }), client.on('errorOccurred', error => this.notifyBackgroundError({ operation: 'connect_channel', resource: client.relayId, error })));
        }
        observed.selected = new Set(selected); observed.subscription.update(channelSubscriptionRequestCodec.encode({ channelIds: selected }));
    }
    async #refresh(signal: AbortSignal): Promise<void> {
        try { for (;;) { await this.#pulse!.wait(signal);
            try {
                await this.#recover(signal); const records = await this.#repository.followed(undefined, signal); const relays = new Set(records.map(record => record.channel.relayId));
                for (const [id, observed] of this.#subscriptions) if (!relays.has(id)) { observed.selected.clear(); observed.subscription.update({ channel_ids: [] }); }
                for (const id of relays) {
                    try {
                        const client = await this.#hosting(id, signal); const channels = records.filter(record => record.channel.relayId === id);
                        try { await this.#subscribe(client, channels.map(record => record.channel.channelId), signal); } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'subscribe_channel', resource: id, error }); }
                        for (const record of channels) {
                            try { await this.#writes.run(async () => { const changed = await this.#repository.saveDescriptor(record.channel, await this.#resolve(client, record.channel, undefined, signal), undefined, signal); if (changed) this.#notify('channelChanged', changed);
                                await this.#synchronize(client, record.channel, (await this.#repository.get(record.channel, signal)).syncSequence, true, signal); }, signal);
                            } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'synchronize_channel', resource: record.channel.channelId, error }); }
                        }
                    } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'connect_channel', resource: id, error }); }
                }
            } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'synchronize_channels', error }); }
        } } catch (error) { if (!signal.aborted) throw error; }
    }
    protected override async onStop(): Promise<void> {
        try { await Promise.all(this.#jobs); } finally { for (const observed of this.#subscriptions.values()) for (const detach of observed.detach) detach(); await Promise.all([...this.#subscriptions.values()].map(observed => observed.subscription.dispose())); this.#subscriptions.clear(); this.#jobs = []; this.#pulse = undefined; }
    }
    protected override async onDispose(): Promise<void> { this.#events.clear(); }
}
