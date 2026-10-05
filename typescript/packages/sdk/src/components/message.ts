import { encryptMessage, decryptMessage } from '../crypto/messages.js';
import { systemRandom, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { certificateId, accountDeviceStateCodec, accountRouteCodec, authorizedDevice, validateDeviceState } from '../models/identity.js';
import { createIdentifier, validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId, validateRelayId } from '../identity/neo.js';
import type { SecretProtector } from '../interactions.js';
import { accountContactSyncCodec, contactAuthorizationCodec, contactConsentCodec, contactGrantCodec, verifyContactGrant, verifyContactInvite,
    type ContactAuthorization, type ContactGrant, type ContactInvite } from '../models/contacts.js';
import { directMessageCodec, messageDeliveryStatusCodec, messageSendRequestCodec, messageTimelineChangedCodec, validateDirectMessage, validateMessageTimelineChanged, type DirectMessage } from '../models/messages.js';
import { MessageContacts, ContactMessageRejection, cachedDeviceStateKey, contactKey, contactRequestKey, readContact, readContactRequest,
    type ContactChange, type ContactRequestChange, type ContactChanges, type ContactInfo, type ContactRequestDirection, type ContactRequestInfo, type PreparedContactSend } from '../messages/contacts.js';
import { MessageOutbox, decodeOutbox, outboxStatus, type MessageSendState, type MessageSendStatus, type OutboxChange } from '../messages/outbox.js';
import { MessageRepository, snapshotReader, type AccountMessage, type MessageInfo, type MessageKey } from '../messages/repository.js';
import { MessageReceiver, type ReceptionResult } from '../messages/receiver.js';
import { registerAccountSender } from '../messages/account-sender.js';
import { requireObject, type JsonObject } from '../protocol/json.js';
import type { ResourceSyncStatus } from '../models/resource-sync.js';
import { historyArguments, type HistoryRange } from '../models/history.js';
import { ResourceSyncTracker } from '../runtime/resource-sync.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { AsyncPulse } from '../runtime/async-pulse.js';
import { awaitWithSignal, throwIfAborted } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import type { MeshlineStore, QueryReader, RecordKey } from '../storage/store.js';
import { readSignedRequest, requestKey } from '../storage/signed-request.js';
import type { RelayClientPool } from '../transport/pool.js';
import type { RelayClient } from '../transport/client.js';
import type { AccountManager } from './account.js';
import type { DeviceManager } from './device.js';
import { ClientComponent, type ClientOptions } from './component.js';

export interface MessageManagerOptions extends ClientOptions {
    readonly store: MeshlineStore; readonly relayClients: RelayClientPool; readonly accountManager: AccountManager; readonly deviceManager: DeviceManager;
    readonly secretProtector?: SecretProtector; readonly random?: RandomSource;
}
export interface MessageEvents {
    readonly syncStatusChanged: ResourceSyncStatus;
    readonly timelineChanged: undefined; readonly messageReceived: readonly MessageInfo[]; readonly sendStatusChanged: MessageSendStatus;
    readonly contactChanged: ContactChange;
    readonly contactRequestChanged: ContactRequestChange;
}
export interface OutboxQuery { readonly recipient?: string; readonly states?: readonly MessageSendState[] }
export interface ContactRequestQuery { readonly accountId?: string; readonly direction?: ContactRequestDirection }
const sendStates = new Set<MessageSendState>(['queued', 'submitting', 'submissionUnknown', 'relayAccepted', 'targetAccepted', 'failed', 'canceled']);
const routeKey = (account: string): RecordKey => ({ collection: 'account_routes', key: account });

/** Account messaging and contact relationships share durable authorization, delivery and synchronization transactions. */
export class MessageManager extends ClientComponent {
    readonly #syncStatus = new ResourceSyncTracker(this.clock, status => this.#notify('syncStatusChanged', status));
    readonly #synchronization = new AsyncGate();
    /** Performs a fresh incremental pass without requiring start(); returns this pass's result and propagates operational failures. */
    synchronize(relayId: string, signal?: AbortSignal): Promise<ResourceSyncStatus> {
        validateRelayId(relayId); return awaitWithSignal(this.runOperation(scope => this.#synchronizePass(relayId, scope), signal), signal);
    }
    /** Reads an in-memory resource snapshot without network access. Completion times reset with a new component instance. */
    getSyncStatus(relayId: string, signal?: AbortSignal): Promise<ResourceSyncStatus> {
        validateRelayId(relayId); return this.runOperation(async () => this.#syncStatus.get(relayId), signal);
    }
    readonly #store: MeshlineStore; readonly #pool: RelayClientPool; readonly #account: AccountManager; readonly #device: DeviceManager; readonly #random: RandomSource;
    readonly #events = new EventHub<MessageEvents>(); readonly #writes = new AsyncGate();
    readonly #repository: MessageRepository; readonly #contacts: MessageContacts; readonly #outbox: MessageOutbox; readonly #receiver: MessageReceiver;
    readonly #deliveryGrants = new Map<string, ContactGrant>();
    readonly #observed = new Map<string, { client: RelayClient; detach: (() => void)[] }>();
    readonly #pendingRoutes = new Set<string>(); #routeRecording = Promise.resolve(); #removeRouteListener: (() => void) | undefined;
    #sendPulse: AsyncPulse | undefined; #syncPulse: AsyncPulse | undefined; #maintenancePulse: AsyncPulse | undefined; #runtimeJobs: Promise<void>[] = [];
    #pendingDeviceRevision: number | undefined; #deviceListeners: (() => void)[] = [];
    constructor(options: MessageManagerOptions) {
        super(options);
        for (const dependency of [options.relayClients, options.accountManager, options.deviceManager]) if (dependency.accountId !== this.accountId || dependency.context.toString() !== this.context.toString())
            throw new ProtocolError('invalid_context', 'Message dependencies belong to another account or network.');
        this.#store = options.store; this.#pool = options.relayClients; this.#account = options.accountManager; this.#device = options.deviceManager; this.#random = options.random ?? systemRandom;
        this.#repository = new MessageRepository({ store: this.#store, context: this.context, accountId: this.accountId, deviceId: () => certificateId(this.#device.certificate, this.context), clock: this.clock,
            ...(options.secretProtector ? { secretProtector: options.secretProtector } : {}) });
        this.#contacts = new MessageContacts({ store: this.#store, context: this.context, accountId: this.accountId, device: this.#device, clock: this.clock,
            getState: (target, signal) => this.#getState(target, signal), prepareSend: (recipient, payload, authorization, devices, signal) => this.#prepareSend(recipient, payload, authorization, devices, signal),
            changed: changes => this.#contactChanges(changes) });
        this.#outbox = new MessageOutbox(this.#store, this.clock, {
            getHome: async signal => { const route = await this.#account.getRoute(undefined, signal); if (!route) throw new ProtocolError('route_required', 'The account has no current route.'); return route.relayId; },
            currentHome: () => this.#account.route?.relayId,
            prepare: async (relayId, signal) => { const relay = await this.#pool.get(relayId, { mode: 'device', signer: this.#device }, signal); return {
                send: async (request, token) => messageDeliveryStatusCodec.decode((await relay.requestHttp('POST', 'message.send', messageSendRequestCodec.encode(request), { ...(token ? { signal: token } : {}) }))!),
                status: async (messageId, token) => messageDeliveryStatusCodec.decode((await relay.requestHttp('GET', 'message.delivery.status', { message_id: messageId }, { ...(token ? { signal: token } : {}) }))!),
            }; },
        }, this.#contacts.deliveryEffects(this.#deliveryGrants));
        this.#receiver = new MessageReceiver(this.#repository, this.context, this.#device, { prepare: (entry, payload, signal) => this.#contacts.prepareReception(entry, payload, signal),
            isPermanentRejection: (error): error is ContactMessageRejection => error instanceof ContactMessageRejection });
        registerAccountSender(this, async (payload, devices, signal, effects) => {
            await this.runOperation(scope => this.#writes.run(async () => {
                const prepared = await this.#prepareSend(this.accountId, payload, undefined, devices, scope);
                await this.#repository.enqueue(prepared.outbox, undefined, prepared.guards, effects, scope);
            }, scope), signal);
            this.#sendPulse?.pulse();
        });
    }
    on<K extends keyof MessageEvents>(event: K, listener: EventListener<MessageEvents[K]>): () => void { return this.#events.on(event, listener); }
    #notify<K extends keyof MessageEvents>(event: K, value: MessageEvents[K]): void {
        this.#events.notify(event, value, error => this.notifyBackgroundError({ operation: 'observer', resource: event, error }));
    }
    #contactChanges(changes: ContactChanges): void {
        for (const change of changes.contacts) this.#notify('contactChanged', change);
        for (const change of changes.requests) this.#notify('contactRequestChanged', change);
        this.#sendPulse?.pulse(); this.#maintenancePulse?.pulse();
    }
    protected override async onInitialize(signal: AbortSignal): Promise<void> {
        this.#account.ensureInitialized(); this.#device.ensureInitialized(); await this.#store.initialize({ context: this.context.toString(), accountId: this.accountId }, signal);
        await this.#outbox.recover(signal);
        for (const row of (await this.#store.read([{ collection: 'message_timelines' }], signal)).sets[0]!)
            if (row.value.hasRetentionGap === true) this.#syncStatus.observeGap(row.key, false);
        this.#removeRouteListener?.(); this.#removeRouteListener = this.#account.on('accountChanged', value => { if (value.route) this.#observeRoute(value.route.relayId); });
        if (this.#account.route) this.#observeRoute(this.#account.route.relayId); await this.#routeRecording;
    }
    #observeRoute(relayId: string): void {
        this.#pendingRoutes.add(relayId);
        this.#routeRecording = this.#routeRecording.then(async () => {
            for (const relay of [...this.#pendingRoutes]) { try { await this.#repository.observeRelay(relay); this.#pendingRoutes.delete(relay); }
                catch (error) { this.notifyBackgroundError({ operation: 'record_route', resource: relay, error }); } }
            this.#sendPulse?.pulse(); this.#syncPulse?.pulse();
        });
    }
    async #getState(target: string | ContactAuthorization, signal?: AbortSignal) {
        const state = await this.#device.getDeviceState(target, signal); if (!state) throw new ProtocolError('device_state_required', 'The requested account has no device state.'); return state;
    }
    async #prepareSend(recipient: string, payload: JsonObject, explicit: ContactAuthorization | undefined, deviceIds: readonly string[] | undefined, signal?: AbortSignal): Promise<PreparedContactSend> {
        validateAccountId(recipient); const self = recipient === this.accountId;
        const contactQueries = self ? [] : [contactKey(recipient), contactRequestKey(recipient, 'incoming')];
        const before = await this.#store.read(contactQueries, signal);
        const contact = self ? undefined : readContact(before.sets[0]![0]?.value); const incoming = self ? undefined : readContactRequest(before.sets[1]![0]?.value);
        const authorization = explicit ?? (contact?.record.status === 'active' ? contact.record.grantFromContact : undefined) ?? incoming?.consent.grant;
        if (payload['$type'] === 'meshline.message.direct' && !self && (!authorization || !('grantor' in authorization))) throw new ProtocolError('contact_required', 'A direct message requires a contact grant from its recipient.');
        await this.#getState(this.accountId, signal); if (!self) await this.#getState(authorization ?? recipient, signal);
        const queries = [routeKey(this.accountId), cachedDeviceStateKey(this.accountId), ...(self ? [] : [cachedDeviceStateKey(recipient)]), requestKey('device.state.publish')]; const snapshot = await this.#store.read(queries, signal);
        const route = accountRouteCodec.decode(snapshot.sets[0]![0]?.value!); const own = accountDeviceStateCodec.decode(snapshot.sets[1]![0]?.value!);
        const remote = self ? own : accountDeviceStateCodec.decode(snapshot.sets[2]![0]?.value!); const now = this.clock.nowSeconds();
        validateDeviceState(own, this.context); validateDeviceState(remote, this.context);
        const pendingState = readSignedRequest(snapshot.sets.at(-1)![0]?.value);
        if (pendingState?.pending) {
            const proposed = accountDeviceStateCodec.decode(pendingState.document); const ids = new Set(proposed.certificates.map(value => certificateId(value, this.context)));
            if (own.certificates.some(value => !ids.has(certificateId(value, this.context)))) throw new ProtocolError('device_removal_pending', 'Resolve the pending device removal before preparing new messages or grants.');
        }
        if (route.account !== this.accountId || own.account !== this.accountId || remote.account !== recipient || route.expiresAt <= now) throw new ProtocolError('invalid_authorization', 'Message routing or device state is outside its account scope or validity.');
        authorizedDevice(own, certificateId(this.#device.certificate, this.context), this.context, now);
        if (authorization) { if ('grantor' in authorization) verifyContactGrant(authorization, remote, this.context, now); else verifyContactInvite(authorization, remote, this.context, now); }
        let recipients = remote.certificates.filter(value => value.notBefore <= now && now < value.expiresAt);
        if (deviceIds) { const selected = new Set(deviceIds); recipients = recipients.filter(value => selected.has(certificateId(value, this.context)));
            if (recipients.length !== selected.size) throw new ProtocolError('unauthorized_device', 'A selected receiving device is no longer authorized.'); }
        const request = await encryptMessage({ context: this.context, signer: this.#device, messageId: createIdentifier('message', this.#random), createdAt: now, recipient, payload,
            recipientDevices: recipients, ...(self ? {} : { senderDevices: own.certificates.filter(value => value.notBefore <= now && now < value.expiresAt) }),
            ...(authorization ? { authorization } : {}), random: this.#random, ...(signal ? { signal } : {}) });
        return { outbox: { request, relayId: route.relayId, state: 'queued', nextAttemptAt: now, isDirect: payload['$type'] === 'meshline.message.direct' },
            guards: [...queries.map((query, index) => ({ query, value: snapshot.sets[index]![0]?.value })), ...contactQueries.map((query, index) => ({ query, value: before.sets[index]![0]?.value }))] };
    }
    async sendMessage(recipient: string, draft: DirectMessage, signal?: AbortSignal): Promise<MessageSendStatus> {
        const payload = directMessageCodec.encode(draft); validateDirectMessage(directMessageCodec.decode(payload));
        const status = await this.runOperation(scope => this.#writes.run(async () => { const prepared = await this.#prepareSend(recipient, payload, undefined, undefined, scope);
            const message = await this.#repository.prepare(prepared.outbox.request.envelope, payload, scope); await this.#repository.enqueue(prepared.outbox, message, prepared.guards, undefined, scope); return outboxStatus(prepared.outbox); }, scope), signal);
        this.#sendPulse?.pulse(); this.#notify('timelineChanged', undefined); this.#notify('sendStatusChanged', status); return status;
    }
    getMessage(key: MessageKey, signal?: AbortSignal): Promise<MessageInfo | undefined> { const copy = { ...key }; return this.runOperation(scope => this.#repository.get(copy, scope), signal); }
    /** Opens a fixed local snapshot in ascending localSequence order. Does not synchronize or mark read. */
    getMessageHistory(peer: string | undefined, signal: AbortSignal | undefined): Promise<QueryReader<MessageInfo>>;
    /** Opens a fixed local snapshot within optional exclusive bounds. Omitted/null range is unbounded. before reads older batches; each batch is ascending. */
    getMessageHistory(peer?: string, range?: HistoryRange | null, signal?: AbortSignal): Promise<QueryReader<MessageInfo>>;
    getMessageHistory(peer?: string, rangeOrSignal?: HistoryRange | AbortSignal | null, signal?: AbortSignal): Promise<QueryReader<MessageInfo>> {
        const args = historyArguments(rangeOrSignal, signal);
        return this.runOperation(scope => this.#repository.history(peer, scope, args.range), args.signal);
    }
    readTimeline(after: number, count: number, signal?: AbortSignal): Promise<readonly AccountMessage[]> { return this.runOperation(scope => this.#repository.readTimeline(after, count, scope), signal); }
    getOutbox(query: OutboxQuery = {}, signal?: AbortSignal): Promise<QueryReader<MessageSendStatus>> {
        const recipient = query.recipient; const states = query.states && [...query.states]; if (recipient !== undefined) validateAccountId(recipient);
        if (states?.some(value => !sendStates.has(value))) throw new TypeError('Unknown message send state.');
        return this.runOperation(async scope => { const rows = await this.#store.read([{ collection: 'message_outbox' }], scope);
            return snapshotReader(rows.sets[0]!.map(decodeOutbox).filter(value => value.isDirect && (recipient === undefined || value.request.envelope.to === recipient) && (states === undefined || states.includes(value.state)))
                .sort((a, b) => a.request.envelope.createdAt - b.request.envelope.createdAt || ordinal(a.request.envelope.messageId, b.request.envelope.messageId)).map(outboxStatus)); }, signal);
    }
    getSendStatus(messageId: string, signal?: AbortSignal): Promise<MessageSendStatus | undefined> { return this.runOperation(async scope => { const record = await this.#outbox.get(messageId, scope); return record && outboxStatus(record); }, signal); }
    async cancelMessage(messageId: string, signal?: AbortSignal): Promise<boolean> { const change = await this.runOperation(scope => this.#outbox.cancel(messageId, scope), signal); if (change) await this.#sendChanges([change]); return Boolean(change); }
    createInvite(expiresAt: number, signal?: AbortSignal): Promise<ContactInvite> { return this.runOperation(scope => this.#contacts.invite(expiresAt, scope), signal); }
    addContact(target: string | ContactInvite, note?: string, signal?: AbortSignal): Promise<ContactRequestInfo> {
        const copy = typeof target === 'string' ? target : contactAuthorizationCodec.decode(contactAuthorizationCodec.encode(target)) as ContactInvite;
        return this.runOperation(scope => this.#writes.run(() => this.#contacts.add(copy, note, scope), scope), signal);
    }
    acceptContactRequest(account: string, signal?: AbortSignal): Promise<ContactInfo> { return this.runOperation(scope => this.#writes.run(() => this.#contacts.accept(account, scope), scope), signal); }
    dismissContactRequest(account: string, signal?: AbortSignal): Promise<void> { return this.runOperation(scope => this.#contacts.dismiss(account, scope), signal); }
    getContacts(search?: string, signal?: AbortSignal): Promise<QueryReader<ContactInfo>> { return this.runOperation(scope => this.#contacts.list(search, scope), signal); }
    getContact(account: string, signal?: AbortSignal): Promise<ContactInfo | undefined> { return this.runOperation(scope => this.#contacts.get(account, scope), signal); }
    getContactRequests(query: ContactRequestQuery = {}, signal?: AbortSignal): Promise<QueryReader<ContactRequestInfo>> { const { accountId, direction } = query; return this.runOperation(scope => this.#contacts.requests(accountId, direction, scope), signal); }
    setContactAlias(account: string, alias: string | null, signal?: AbortSignal): Promise<ContactInfo> { return this.runOperation(scope => this.#writes.run(() => this.#contacts.edit(account, { alias }, scope), scope), signal); }
    removeContact(account: string, signal?: AbortSignal): Promise<void> { return this.runOperation(scope => this.#writes.run(() => this.#contacts.edit(account, { deleted: true }, scope), scope), signal); }
    protected override async onStart(signal: AbortSignal): Promise<void> {
        const own = await this.#getState(this.accountId, signal); authorizedDevice(own, certificateId(this.#device.certificate, this.context), this.context, this.clock.nowSeconds());
        const initial = await this.#prepareSend(this.accountId, accountContactSyncCodec.encode({ records: [], requestSnapshot: true }), undefined, undefined, signal);
        await this.#repository.enqueue(initial.outbox, undefined, initial.guards, undefined, signal);
        this.#sendPulse = new AsyncPulse(); this.#syncPulse = new AsyncPulse(); this.#maintenancePulse = new AsyncPulse(); this.#pendingDeviceRevision = own.revision;
        this.#deviceListeners = [this.#device.on('deviceChanged', () => { this.#maintenancePulse?.pulse(); }), this.#device.on('deviceStateChanged', value => {
            if (value.account === this.accountId) { this.#pendingDeviceRevision = Math.max(value.revision, this.#pendingDeviceRevision ?? -1); this.#maintenancePulse?.pulse(); }
        })];
        this.#runtimeJobs = [this.#sendLoop(this.runtimeSignal), this.#syncLoop(this.runtimeSignal), this.#poll(this.runtimeSignal), this.#maintainContacts(this.runtimeSignal), this.#pollContacts(this.runtimeSignal)];
        for (const job of this.#runtimeJobs) job.catch(() => undefined); this.#sendPulse.pulse(); this.#syncPulse.pulse(); this.#maintenancePulse.pulse();
    }
    async #poll(signal: AbortSignal): Promise<void> { try { for (;;) { await this.clock.delay(15_000, signal); this.#sendPulse?.pulse(); this.#syncPulse?.pulse(); } } catch (error) { if (!signal.aborted) throw error; } }
    async #pollContacts(signal: AbortSignal): Promise<void> { try { for (;;) { await this.clock.delay(30_000, signal); this.#maintenancePulse?.pulse(); } } catch (error) { if (!signal.aborted) throw error; } }
    async #maintainContacts(signal: AbortSignal): Promise<void> {
        try { for (;;) {
            await this.#maintenancePulse!.wait(signal); const revision = this.#pendingDeviceRevision; let failed = false;
            try {
                const rows = await this.#store.read([{ collection: 'contacts' }], signal);
                for (const row of rows.sets[0]!) {
                    if (readContact(row.value)?.record.status !== 'active') continue;
                    try { await this.#contacts.maintain(row.key, revision, signal); }
                    catch (error) { throwIfAborted(signal); failed = true; this.notifyBackgroundError({ operation: 'maintain_contact', resource: row.key, error }); }
                }
                if (!failed && this.#pendingDeviceRevision === revision) this.#pendingDeviceRevision = undefined;
            } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'maintain_contacts', error }); }
        } } catch (error) { if (!signal.aborted) throw error; }
    }
    async #sendLoop(signal: AbortSignal): Promise<void> {
        try { for (;;) {
            await this.#sendPulse!.wait(signal);
            try { for (const messageId of await this.#outbox.due(signal)) {
                try {
                const record = await this.#outbox.get(messageId, signal); if (!record) continue;
                if (!record.isDirect && record.request.envelope.to !== this.accountId) {
                    const box = record.request.senderBoxes?.find(value => value.deviceId === certificateId(this.#device.certificate, this.context));
                    if (!box) throw new ProtocolError('missing_key_box', 'Outgoing protocol message has no local sender key box.');
                    const payload = await decryptMessage({ context: this.context, receiver: this.#device, sender: this.#device.certificate, envelope: record.request.envelope, keyBox: box, signal });
                    const grant = payload['$type'] === 'meshline.contact.consent' ? contactConsentCodec.decode(payload).grant : payload['$type'] === 'meshline.contact.grant' ? contactGrantCodec.decode(payload) : undefined;
                    if (grant) this.#deliveryGrants.set(messageId, grant);
                }
                const result = await this.#outbox.process(messageId, signal); await this.#sendChanges(result.changes); if (result.error && !signal.aborted) this.notifyBackgroundError({ operation: 'send_message', resource: messageId, error: result.error });
                } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'send_message', resource: messageId, error }); }
                finally { this.#deliveryGrants.delete(messageId); }
            } } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'send_message', error }); }
        } } catch (error) { if (!signal.aborted) throw error; }
    }
    async #sendChanges(changes: readonly OutboxChange[]): Promise<void> {
        for (const { current } of changes) {
            if (current.isDirect) this.#notify('sendStatusChanged', outboxStatus(current));
        }
    }
    async #syncLoop(signal: AbortSignal): Promise<void> {
        try { for (;;) {
            await this.#syncPulse!.wait(signal);
            try {
                try { await this.#account.getRoute(undefined, signal); } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'resolve_route', error }); }
                await this.#routeRecording;
                const rows = await this.#store.read([{ collection: 'message_timelines' }], signal);
                for (const row of rows.sets[0]!) {
                    if (row.value.hasRetentionGap === true) this.#syncStatus.observeGap(row.key);
                    try { await this.#synchronizePass(row.key, signal, true);
                    } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'synchronize_messages', resource: row.key, error }); }
                }
            } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'synchronize_messages', error }); }
        } } catch (error) { if (!signal.aborted) throw error; }
    }
    #synchronizePass(relayId: string, signal: AbortSignal, observe = false): Promise<ResourceSyncStatus> {
        return this.#synchronization.run(() => this.#syncStatus.run(relayId, async () => {
            const client = await this.#pool.get(relayId, { mode: 'device', signer: this.#device }, signal);
            if (observe) {
                try { await this.#observeClient(client, signal); } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'connect', resource: relayId, error }); }
            }
            await this.#receiver.synchronize(relayId, { read: async (after, scope) => (await client.requestHttp('GET', 'message.timeline.sync', { after }, { ...(scope ? { signal: scope } : {}) }))! }, result => this.#received(relayId, result), signal, () => this.#syncStatus.observeGap(relayId));
            return undefined;
        }, signal, !observe), signal);
    }
    async #observeClient(client: RelayClient, signal: AbortSignal): Promise<void> {
        const previous = this.#observed.get(client.relayId); if (previous?.client === client) return;
        const descriptor = await client.getDescriptor(signal); const detach = [client.on('notificationReceived', request => {
            if (request.method !== 'message.timeline.changed') return; validateMessageTimelineChanged(messageTimelineChangedCodec.decode(requireObject(request.params!))); this.#syncPulse?.pulse();
        }), client.on('socketConnected', () => { this.#syncPulse?.pulse(); }), client.on('errorOccurred', error => this.notifyBackgroundError({ operation: 'connect', resource: client.relayId, error }))];
        for (const remove of previous?.detach ?? []) remove(); this.#observed.set(client.relayId, { client, detach });
        if (descriptor.endpoints.some(value => value.startsWith('wss://'))) client.startNotifications();
    }
    #received(relayId: string, result: ReceptionResult): void {
        if (result.inserted) this.#notify('timelineChanged', undefined); if (result.messages.length) this.#notify('messageReceived', result.messages);
        for (const rejection of result.rejected) this.notifyBackgroundError({ operation: 'reject_message', resource: `${relayId}/${rejection.sequence}`, error: rejection.error });
        this.#sendPulse?.pulse();
    }
    protected override async onStop(): Promise<void> {
        try { const results = await Promise.allSettled(this.#runtimeJobs); const failures = results.filter((value): value is PromiseRejectedResult => value.status === 'rejected'); if (failures.length) throw new AggregateError(failures.map(value => value.reason), 'Message runtime failed.'); }
        finally { for (const observed of this.#observed.values()) for (const remove of observed.detach) remove(); this.#observed.clear();
            for (const remove of this.#deviceListeners) remove(); this.#deviceListeners = []; this.#runtimeJobs = []; this.#sendPulse = this.#syncPulse = this.#maintenancePulse = undefined; this.#pendingDeviceRevision = undefined; this.#syncStatus.stop(); }
    }
    protected override async onDispose(): Promise<void> { this.#removeRouteListener?.(); this.#removeRouteListener = undefined; await this.#routeRecording; if (this.#pendingRoutes.size) throw new Error('Some observed message timelines could not be persisted.'); this.#syncStatus.stop(true); this.#events.clear(); }
}
function ordinal(a: string, b: string): number { return a < b ? -1 : a > b ? 1 : 0; }
