import { ProtocolError, StateConflictError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import type { DeviceSigner } from '../interactions.js';
import { accountContactSyncCodec, contactConsentCodec, contactGrantCodec, contactGrantInput, contactInviteCodec, contactInviteInput, contactRecordCodec,
    filterContactGrantSignatures, selectContactGrant, validateAccountContactSync, validateContactConsent, validateContactInvite, validateContactRecord, verifyContactGrant,
    type AccountContactSync, type ContactAuthorization, type ContactConsent, type ContactGrant, type ContactInvite, type ContactRecord } from '../models/contacts.js';
import { accountDeviceStateCodec, authorizedDevice, certificateId, validateDeviceState, type AccountDeviceState } from '../models/identity.js';
import { deviceStateChangedCodec, validateDeviceStateChanged, type MessageTimelineEntry } from '../models/messages.js';
import type { NetworkContext } from '../protocol/context.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { canonicalJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import type { RuntimeClock } from '../runtime/clock.js';
import type { MeshlineStore, QueryReader, RecordKey, StoreMutation, StoreSnapshot } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { decodeOutbox, encodeOutbox, outboxKey, type MessageSendState, type OutboxEffects, type OutboxRecord } from './outbox.js';
import { snapshotReader, type MessageEffects, type RecordGuard } from './repository.js';

export type ContactGrantState = 'unknown' | 'missing' | 'valid' | 'expired' | 'insufficientSignatures';
export interface ContactInfo { readonly accountId: string; readonly alias?: string; readonly state: 'active'; readonly grantFromContact: ContactGrantState; readonly grantToContact: ContactGrantState; readonly updatedAt: number }
export type ContactRequestDirection = 'incoming' | 'outgoing';
export interface ContactRequestInfo { readonly accountId: string; readonly direction: ContactRequestDirection; readonly createdAt: number; readonly note?: string; readonly messageId: string; readonly sendState?: MessageSendState }
export type ContactChangeKind = 'relationship' | 'alias' | 'authorization' | 'deleted';
export interface ContactChange { readonly accountId: string; readonly contact?: ContactInfo; readonly kinds: readonly ContactChangeKind[] }
export type ContactRequestChangeKind = 'added' | 'updated' | 'removed';
export interface ContactRequestChange { readonly accountId: string; readonly direction: ContactRequestDirection; readonly request?: ContactRequestInfo; readonly kind: ContactRequestChangeKind }
export interface ContactChanges { readonly contacts: readonly ContactChange[]; readonly requests: readonly ContactRequestChange[] }
export interface StoredContact { readonly record: ContactRecord; readonly confirmedGrantTo?: ContactGrant; readonly requiredDeviceRevision?: number }
export interface StoredContactRequest extends ContactRequestInfo { readonly consent: ContactConsent }
export interface PreparedContactSend { readonly outbox: OutboxRecord; readonly guards: readonly RecordGuard[] }
export interface ContactHost {
    readonly store: MeshlineStore; readonly context: NetworkContext; readonly accountId: string; readonly device: DeviceSigner; readonly clock: RuntimeClock;
    getState(target: string | ContactAuthorization, signal?: AbortSignal): Promise<AccountDeviceState>;
    prepareSend(recipient: string, payload: JsonObject, authorization: ContactAuthorization | undefined, recipientDevices: readonly string[] | undefined, signal?: AbortSignal): Promise<PreparedContactSend>;
    changed(changes: ContactChanges): void;
}
export const contactKey = (account: string): RecordKey => ({ collection: 'contacts', key: account });
export const contactRequestKey = (account: string, direction: ContactRequestDirection): RecordKey => ({ collection: 'contact_requests', key: `${account}|${direction}` });
export const cachedDeviceStateKey = (account: string): RecordKey => ({ collection: 'device_states', key: account });
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
const recordId = (key: RecordKey): string => `${key.collection}|${key.key}`;
const put = (key: RecordKey, value: JsonObject): StoreMutation => ({ kind: 'put', ...key, value });
const removeRequests = (account: string): StoreMutation[] => ['incoming', 'outgoing'].map(direction => ({ kind: 'delete', ...contactRequestKey(account, direction as ContactRequestDirection) }));
const sends = new Set<MessageSendState>(['queued', 'submitting', 'submissionUnknown', 'relayAccepted', 'targetAccepted', 'failed', 'canceled']);
export function readContact(value: JsonObject | undefined): StoredContact | undefined {
    if (!value) return undefined;
    const record = contactRecordCodec.decode(value.record!);
    if (value.requiredDeviceRevision !== undefined) requireSafeInteger(value.requiredDeviceRevision, 0);
    return { record, ...(value.confirmedGrantTo === undefined ? {} : { confirmedGrantTo: contactGrantCodec.decode(value.confirmedGrantTo) }),
        ...(value.requiredDeviceRevision === undefined ? {} : { requiredDeviceRevision: value.requiredDeviceRevision }) };
}
export function encodeContact(value: StoredContact): JsonObject {
    return { record: contactRecordCodec.encode(value.record), ...(value.confirmedGrantTo ? { confirmedGrantTo: contactGrantCodec.encode(value.confirmedGrantTo) } : {}),
        ...(value.requiredDeviceRevision === undefined ? {} : { requiredDeviceRevision: value.requiredDeviceRevision }) };
}
export function readContactRequest(value: JsonObject | undefined): StoredContactRequest | undefined {
    if (!value) return undefined;
    if (typeof value.accountId !== 'string' || (value.direction !== 'incoming' && value.direction !== 'outgoing') || typeof value.messageId !== 'string'
        || value.sendState !== undefined && (typeof value.sendState !== 'string' || !sends.has(value.sendState as MessageSendState))) throw new ProtocolError('invalid_storage', 'Invalid stored contact request.');
    validateAccountId(value.accountId); validateIdentifier('message', value.messageId); requireSafeInteger(value.createdAt, 0);
    const consent = contactConsentCodec.decode(value.consent!);
    return { accountId: value.accountId, direction: value.direction, createdAt: value.createdAt, messageId: value.messageId, consent,
        ...(consent.note === undefined ? {} : { note: consent.note }), ...(value.sendState === undefined ? {} : { sendState: value.sendState as MessageSendState }) };
}
export function encodeContactRequest(value: StoredContactRequest): JsonObject {
    return { accountId: value.accountId, direction: value.direction, createdAt: value.createdAt, messageId: value.messageId, consent: contactConsentCodec.encode(value.consent),
        ...(value.sendState === undefined ? {} : { sendState: value.sendState }) };
}
export function requestInfo(value: StoredContactRequest): ContactRequestInfo { const { consent: _consent, ...info } = value; return info; }
export class ContactMessageRejection extends ProtocolError {}
function reject(code: string, message: string): never { throw new ContactMessageRejection(code, message); }
function pureValidation<T>(action: () => T): T {
    try { return action(); } catch (error) { if (error instanceof ProtocolError && !(error instanceof ContactMessageRejection)) throw new ContactMessageRejection(error.code, error.message, { cause: error }); throw error; }
}

/** Contact workflows prepare signatures/encrypted synchronization outside the eventual atomic transaction. */
export class MessageContacts {
    constructor(readonly host: ContactHost) {}
    #checkAccount(account: string): void { validateAccountId(account); if (account === this.host.accountId) throw new ProtocolError('invalid_contact', 'An account cannot add itself as a contact.'); }
    #nextTime(previous = -1): number { const next = Math.max(this.host.clock.nowSeconds(), previous + 1); requireSafeInteger(next, 0); return next; }
    async #snapshot(keys: readonly RecordKey[], signal?: AbortSignal): Promise<readonly RecordGuard[]> {
        const snapshot = await this.host.store.read(keys, signal); return keys.map((query, index) => ({ query, value: snapshot.sets[index]![0]?.value }));
    }
    #effect(guards: readonly RecordGuard[], mutations: readonly StoreMutation[], prepared: readonly PreparedContactSend[] = [], kinds: readonly ContactChangeKind[] = ['relationship', 'authorization']): MessageEffects {
        const all = [...guards, ...prepared.flatMap(value => [...value.guards, { query: outboxKey(value.outbox.request.envelope.messageId), value: undefined }])];
        const expected = new Map<string, RecordGuard>();
        for (const guard of all) { const id = `${guard.query.collection}|${guard.query.key}`; const previous = expected.get(id);
            if (previous && fingerprint(previous.value) !== fingerprint(guard.value)) throw new StateConflictError('Contact preparation used inconsistent snapshots.'); expected.set(id, guard); }
        const unique = [...expected.values()];
        const queries = new Map(unique.map(value => [recordId(value.query), value.query]));
        for (const mutation of mutations) { queries.set(recordId(mutation), { collection: mutation.collection, key: mutation.key });
            if (mutation.collection === 'contacts') { const key = cachedDeviceStateKey(mutation.key); queries.set(recordId(key), key); } }
        const own = cachedDeviceStateKey(this.host.accountId); queries.set(recordId(own), own);
        return { queries: [...queries.values()], committed: (snapshot, changes) => this.host.changed(this.#changes(snapshot, changes, kinds)), plan: snapshot => {
            for (let index = 0; index < unique.length; index++) if (fingerprint(snapshot.sets[index]![0]?.value) !== fingerprint(unique[index]!.value)) throw new StateConflictError('Contact state changed while preparing signed messages.');
            return [...mutations, ...prepared.map(value => put(outboxKey(value.outbox.request.envelope.messageId), encodeOutbox(value.outbox)))];
        } };
    }
    async #commit(effect: MessageEffects, signal?: AbortSignal): Promise<StoreSnapshot> {
        const result = await updateStore(this.host.store, effect.queries, snapshot => { const mutations = effect.plan(snapshot); return { mutations, result: { snapshot, mutations } }; }, signal);
        effect.committed?.(result.snapshot, result.mutations);
        return result.snapshot;
    }
    #changes(snapshot: StoreSnapshot, mutations: readonly StoreMutation[], kinds: readonly ContactChangeKind[]): ContactChanges {
        const before = new Map(snapshot.sets.flat().map(row => [recordId(row), row.value])); const after = new Map(before);
        for (const mutation of mutations) { if (mutation.kind === 'put') after.set(recordId(mutation), mutation.value); else after.delete(recordId(mutation)); }
        const contacts: ContactChange[] = []; const requests: ContactRequestChange[] = []; const visited = new Set<string>();
        for (const mutation of mutations) {
            const id = recordId(mutation); if (visited.has(id) || fingerprint(before.get(id)) === fingerprint(after.get(id))) continue; visited.add(id);
            if (mutation.collection === 'contacts' && kinds.length) {
                const contact = readContact(after.get(id));
                contacts.push({ accountId: mutation.key, kinds: contact?.record.status === 'active' ? [...kinds] : ['deleted'],
                    ...(contact?.record.status === 'active' ? { contact: this.#info(contact, state(after.get(recordId(cachedDeviceStateKey(this.host.accountId)))), state(after.get(recordId(cachedDeviceStateKey(mutation.key))))) } : {}) });
            }
            if (mutation.collection === 'contact_requests') {
                const previous = readContactRequest(before.get(id)); const current = readContactRequest(after.get(id)); const request = current ?? previous;
                if (request) requests.push({ accountId: request.accountId, direction: request.direction,
                    kind: !current ? 'removed' : !previous || current.direction === 'outgoing' && current.messageId !== previous.messageId ? 'added' : 'updated',
                    ...(current ? { request: requestInfo(current) } : {}) });
            }
        }
        return { contacts, requests };
    }
    #protocolRecord(contact: StoredContact): ContactRecord {
        const now = this.host.clock.nowSeconds(); const { grantFromContact, grantToContact, ...record } = contact.record;
        return { ...record, ...(grantFromContact && (grantFromContact.expiresAt ?? Infinity) > now ? { grantFromContact } : {}),
            ...(grantToContact && (grantToContact.expiresAt ?? Infinity) > now ? { grantToContact } : {}) };
    }
    async #sync(contact: StoredContact, signal?: AbortSignal): Promise<PreparedContactSend> {
        return this.host.prepareSend(this.host.accountId, accountContactSyncCodec.encode({ records: [this.#protocolRecord(contact)] }), undefined, undefined, signal);
    }
    #grantState(grant: ContactGrant | undefined, state: AccountDeviceState | undefined): ContactGrantState {
        if (!grant) return 'missing'; if ((grant.expiresAt ?? Infinity) <= this.host.clock.nowSeconds()) return 'expired'; if (!state) return 'unknown';
        return Object.keys(filterContactGrantSignatures(grant, state, this.host.context, this.host.clock.nowSeconds()).signatures).length ? 'valid' : 'insufficientSignatures';
    }
    #info(contact: StoredContact, own?: AccountDeviceState, remote?: AccountDeviceState): ContactInfo {
        const record = contact.record;
        return { accountId: record.account, ...(record.alias === undefined ? {} : { alias: record.alias }), state: 'active', updatedAt: record.updatedAt,
            grantFromContact: this.#grantState(record.grantFromContact, remote), grantToContact: this.#grantState(record.grantToContact, own) };
    }
    async get(account: string, signal?: AbortSignal): Promise<ContactInfo | undefined> {
        this.#checkAccount(account); const rows = await this.host.store.read([contactKey(account), cachedDeviceStateKey(this.host.accountId), cachedDeviceStateKey(account)], signal);
        const contact = readContact(rows.sets[0]![0]?.value); if (!contact || contact.record.status !== 'active') return undefined;
        return this.#info(contact, state(rows.sets[1]![0]?.value), state(rows.sets[2]![0]?.value));
    }
    async list(search = '', signal?: AbortSignal): Promise<QueryReader<ContactInfo>> {
        const rows = await this.host.store.read([{ collection: 'contacts' }, { collection: 'device_states' }], signal);
        const states = new Map(rows.sets[1]!.map(row => [row.key, state(row.value)!]));
        return snapshotReader(rows.sets[0]!.map(row => readContact(row.value)!).filter(value => value.record.status === 'active' && (!search || value.record.account.includes(search) || value.record.alias?.includes(search)))
            .map(value => this.#info(value, states.get(this.host.accountId), states.get(value.record.account))));
    }
    async requests(account?: string, direction?: ContactRequestDirection, signal?: AbortSignal): Promise<QueryReader<ContactRequestInfo>> {
        if (account !== undefined) this.#checkAccount(account); if (direction !== undefined && direction !== 'incoming' && direction !== 'outgoing') throw new TypeError('Unknown contact request direction.');
        const rows = await this.host.store.read([{ collection: 'contact_requests' }], signal);
        const values = rows.sets[0]!.map(row => readContactRequest(row.value)!).filter(value => (account === undefined || value.accountId === account) && (direction === undefined || value.direction === direction))
            .sort((a, b) => a.createdAt - b.createdAt || ordinal(a.accountId, b.accountId) || ordinal(a.direction, b.direction));
        return snapshotReader(values.map(requestInfo));
    }
    async invite(expiresAt: number, signal?: AbortSignal): Promise<ContactInvite> {
        requireSafeInteger(expiresAt, 0); const id = certificateId(this.host.device.certificate, this.host.context);
        let invitation: ContactInvite = { inviter: this.host.accountId, signerDeviceId: id, expiresAt, deviceSignature: new Uint8Array(64) };
        validateContactInvite(invitation, this.host.clock.nowSeconds());
        invitation = { ...invitation, deviceSignature: await this.host.device.sign(contactInviteInput(invitation, this.host.context), signal) };
        if (certificateId(this.host.device.certificate, this.host.context) !== id) throw new StateConflictError('Device identity changed while signing invitation.');
        return invitation;
    }
    async #signGrant(grant: ContactGrant, signal?: AbortSignal): Promise<ContactGrant> {
        const id = certificateId(this.host.device.certificate, this.host.context); if (grant.signatures[id]) return grant;
        const signature = await this.host.device.sign(contactGrantInput(grant, this.host.context), signal);
        if (certificateId(this.host.device.certificate, this.host.context) !== id) throw new StateConflictError('Device identity changed while signing contact grant.');
        return { ...grant, signatures: { ...grant.signatures, [id]: signature } };
    }
    async #consent(account: string, note: string | undefined, signal?: AbortSignal): Promise<ContactConsent> {
        const own = await this.host.getState(this.host.accountId, signal); authorizedDevice(own, certificateId(this.host.device.certificate, this.host.context), this.host.context, this.host.clock.nowSeconds());
        const grant = await this.#signGrant({ grantor: this.host.accountId, grantee: account, signatures: {} }, signal);
        const consent = { deviceState: own, grant, ...(note === undefined ? {} : { note }) }; validateContactConsent(consent, this.host.context, this.host.clock.nowSeconds()); return consent;
    }
    async add(target: string | ContactInvite, note?: string, signal?: AbortSignal): Promise<ContactRequestInfo> {
        const invitation = typeof target === 'string' ? undefined : contactInviteCodec.decode(contactInviteCodec.encode(target)); const account = typeof target === 'string' ? target : target.inviter;
        this.#checkAccount(account); await this.host.getState(invitation ?? account, signal);
        const consent = await this.#consent(account, note, signal); const guards = await this.#snapshot([contactKey(account), contactRequestKey(account, 'outgoing')], signal);
        if (readContact(guards[0]!.value)?.record.status === 'active') throw new ProtocolError('contact_exists', 'The account is already a contact.');
        const previous = readContactRequest(guards[1]!.value); if (previous && previous.sendState !== 'failed' && previous.sendState !== 'canceled') return requestInfo(previous);
        const prepared = await this.host.prepareSend(account, contactConsentCodec.encode(consent), invitation, undefined, signal); const envelope = prepared.outbox.request.envelope;
        const request: StoredContactRequest = { accountId: account, direction: 'outgoing', createdAt: envelope.createdAt, messageId: envelope.messageId, consent, sendState: 'queued', ...(note === undefined ? {} : { note }) };
        await this.#commit(this.#effect(guards, [put(contactRequestKey(account, 'outgoing'), encodeContactRequest(request))], [prepared]), signal); return requestInfo(request);
    }
    async accept(account: string, signal?: AbortSignal): Promise<ContactInfo> {
        this.#checkAccount(account); const guards = await this.#snapshot([contactKey(account), contactRequestKey(account, 'incoming'), contactRequestKey(account, 'outgoing')], signal);
        const incoming = readContactRequest(guards[1]!.value); if (!incoming) throw new ProtocolError('contact_request_required', 'There is no incoming request from this account.');
        const remote = await this.host.getState(incoming.consent.grant, signal); const grant = this.#verified(incoming.consent.grant, remote);
        const consent = await this.#consent(account, undefined, signal); const previous = readContact(guards[0]!.value);
        const contact: StoredContact = { record: { account, status: 'active', updatedAt: this.#nextTime(previous?.record.updatedAt), ...(previous?.record.alias === undefined ? {} : { alias: previous.record.alias }), grantFromContact: grant, grantToContact: consent.grant } };
        const outgoing = await this.host.prepareSend(account, contactConsentCodec.encode(consent), grant, undefined, signal); const sync = await this.#sync(contact, signal);
        await this.#commit(this.#effect(guards, [put(contactKey(account), encodeContact(contact)), ...removeRequests(account)], [outgoing, sync]), signal);
        return this.#info(contact, consent.deviceState, remote);
    }
    async dismiss(account: string, signal?: AbortSignal): Promise<void> {
        this.#checkAccount(account); const key = contactRequestKey(account, 'incoming');
        await this.#commit({ queries: [key], plan: snapshot => snapshot.sets[0]!.length ? [{ kind: 'delete', ...key }] : [],
            committed: (snapshot, mutations) => this.host.changed(this.#changes(snapshot, mutations, [])) }, signal);
    }
    edit(account: string, change: { readonly alias: string | null }, signal?: AbortSignal): Promise<ContactInfo>;
    edit(account: string, change: { readonly deleted: true }, signal?: AbortSignal): Promise<void>;
    async edit(account: string, change: { readonly alias?: string | null; readonly deleted?: true }, signal?: AbortSignal): Promise<ContactInfo | void> {
        this.#checkAccount(account);
        const initial = await this.#snapshot([contactKey(account), contactRequestKey(account, 'incoming'), contactRequestKey(account, 'outgoing'), cachedDeviceStateKey(this.host.accountId), cachedDeviceStateKey(account)], signal);
        const guards = initial.slice(0, 3);
        const previous = readContact(guards[0]!.value);
        if (!change.deleted && previous?.record.status !== 'active') throw new ProtocolError('contact_required', 'Alias changes require an active contact.');
        const { alias: oldAlias, grantFromContact, grantToContact, ...base } = previous?.record ?? { account, status: 'deleted' as const, updatedAt: 0 };
        const alias = change.alias === null ? undefined : change.alias ?? oldAlias;
        if (!change.deleted && alias === oldAlias) return this.#info(previous!, state(initial[3]!.value), state(initial[4]!.value));
        const record: ContactRecord = { ...base, status: change.deleted ? 'deleted' : 'active', updatedAt: this.#nextTime(previous?.record.updatedAt), ...(alias === undefined ? {} : { alias }),
            ...(!change.deleted && grantFromContact ? { grantFromContact } : {}), ...(!change.deleted && grantToContact ? { grantToContact } : {}) };
        validateContactRecord(record, this.host.clock.nowSeconds()); const contact: StoredContact = change.deleted ? { record } : { ...previous!, record };
        const sync = await this.#sync(contact, signal);
        const committed = await this.#commit(this.#effect(guards, [put(contactKey(account), encodeContact(contact)), ...(change.deleted ? removeRequests(account) : [])], [sync], [change.deleted ? 'deleted' : 'alias']), signal);
        if (!change.deleted) {
            // Return from the successful transaction's snapshot. A fresh read
            // could race another write or be canceled by a disposal observer.
            const devices = new Map(committed.sets.flat().filter(row => row.collection === 'device_states').map(row => [row.key, state(row.value)]));
            return this.#info(contact, devices.get(this.host.accountId), devices.get(account));
        }
    }
    #verified(grant: ContactGrant, devices: AccountDeviceState): ContactGrant { verifyContactGrant(grant, devices, this.host.context, this.host.clock.nowSeconds()); return filterContactGrantSignatures(grant, devices, this.host.context, this.host.clock.nowSeconds()); }
    #merge(previous: ContactGrant | undefined, incoming: ContactGrant, devices: AccountDeviceState): ContactGrant {
        const result = selectContactGrant(previous, incoming, devices, this.host.context, this.host.clock.nowSeconds());
        if (result.action === 'invalid') throw result.error; if (result.action === 'out_of_scope' || !result.grant) throw new ProtocolError('invalid_authorization', 'Contact grants have inconsistent scope.'); return result.grant;
    }
    /** Called only after envelope signature and decryption pass. External lookups remain outside rejection wrapping. */
    async prepareReception(entry: MessageTimelineEntry, payload: JsonObject, signal?: AbortSignal): Promise<MessageEffects | undefined> {
        const envelope = entry.envelope; const type = payload['$type']; const self = envelope.from === envelope.to;
        if (envelope.from === this.host.accountId && !self) return undefined;
        if (['meshline.account.group.state.request', 'meshline.account.group.state.sync', 'meshline.account.group.history_secret.sync'].includes(type as string)) {
            if (!self || envelope.from !== this.host.accountId) reject('invalid_binding', 'Account group synchronization requires self-delivery.');
            await this.host.getState(this.host.accountId, signal); return this.#authorize(entry);
        }
        if (type === 'meshline.contact.consent') {
            if (self) reject('invalid_binding', 'Contact consent cannot be self-delivered.');
            const consent = pureValidation(() => { const value = contactConsentCodec.decode(payload); validateContactConsent(value, this.host.context, this.host.clock.nowSeconds()); return value; });
            return this.#receiveConsent(entry, consent, signal);
        }
        const contacts = ['meshline.contact.grant', 'meshline.device.state.changed', 'meshline.account.contacts.sync'];
        if (contacts.includes(type as string) && self !== (type === 'meshline.account.contacts.sync')) reject('invalid_binding', 'Contact payload has invalid sender/recipient account binding.');
        if (type === 'meshline.account.contacts.sync') return this.#receiveSync(entry, pureValidation(() => { const value = accountContactSyncCodec.decode(payload); validateAccountContactSync(value, this.host.clock.nowSeconds(), this.host.accountId); return value; }), signal);
        if (type !== 'meshline.message.direct' && type !== 'meshline.contact.grant' && type !== 'meshline.device.state.changed') return undefined;
        await this.host.getState(this.host.accountId, signal);
        if (type === 'meshline.contact.grant') {
            const grant = pureValidation(() => contactGrantCodec.decode(payload));
            if (grant.grantor !== envelope.from || grant.grantee !== envelope.to) reject('invalid_binding', 'Contact grant differs from envelope participants.');
            const remote = await this.host.getState(grant, signal); pureValidation(() => this.#verified(grant, remote));
            const guards = await this.#snapshot([contactKey(envelope.from)], signal); const current = readContact(guards[0]!.value);
            if (!current || current.record.status !== 'active') reject('contact_required', 'No active contact exists for this sender.');
            const merged = pureValidation(() => this.#merge(current.record.grantFromContact, grant, remote));
            if (current.record.grantFromContact && contactGrantCodec.stringify(merged) === contactGrantCodec.stringify(current.record.grantFromContact)) return this.#authorize(entry);
            const next = { ...current, record: { ...current.record, grantFromContact: merged, updatedAt: this.#nextTime(current.record.updatedAt) } };
            const sync = await this.#sync(next, signal);
            return this.#authorize(entry, this.#effect(guards, [put(contactKey(envelope.from), encodeContact(next))], [sync], ['authorization']));
        }
        if (type === 'meshline.device.state.changed') {
            const change = pureValidation(() => { const value = deviceStateChangedCodec.decode(payload); validateDeviceStateChanged(value); return value; });
            const key = contactKey(envelope.from);
            return this.#authorize(entry, { queries: [key, cachedDeviceStateKey(envelope.from)], plan: snapshot => {
                const contact = readContact(snapshot.sets[0]![0]?.value); if (!contact || contact.record.status !== 'active') reject('contact_required', 'Contact is no longer active.');
                const remote = state(snapshot.sets[1]![0]?.value); if (remote && remote.revision >= change.revision) return [];
                return [put(key, encodeContact({ ...contact, requiredDeviceRevision: Math.max(change.revision, contact.requiredDeviceRevision ?? -1) }))];
            } });
        }
        return this.#authorize(entry);
    }
    #authorize(entry: MessageTimelineEntry, effect?: MessageEffects): MessageEffects {
        const self = entry.envelope.from === entry.envelope.to; const account = entry.envelope.from;
        return { queries: [cachedDeviceStateKey(this.host.accountId), contactKey(account), ...effect?.queries ?? []],
            ...(effect?.committed ? { committed: (snapshot: StoreSnapshot, mutations: readonly StoreMutation[]) => effect.committed!({ ...snapshot, sets: snapshot.sets.slice(2) }, mutations) } : {}), plan: snapshot => {
            const own = state(snapshot.sets[0]![0]?.value); if (!own) throw new ProtocolError('device_state_required', 'Current own device state is unavailable.');
            pureValidation(() => {
                if (self) authorizedDevice(own, entry.envelope.fromDeviceId, this.host.context, this.host.clock.nowSeconds());
                else { const contact = readContact(snapshot.sets[1]![0]?.value); if (contact?.record.status !== 'active' || !contact.record.grantToContact) reject('contact_required', 'No active locally issued grant remains for this sender.');
                    verifyContactGrant(contact.record.grantToContact, own, this.host.context, this.host.clock.nowSeconds()); }
            });
            return effect?.plan({ ...snapshot, sets: snapshot.sets.slice(2) }) ?? [];
        } };
    }
    async #receiveConsent(entry: MessageTimelineEntry, consent: ContactConsent, signal?: AbortSignal): Promise<MessageEffects> {
        const account = entry.envelope.from;
        pureValidation(() => {
            if (consent.deviceState.account !== account || consent.grant.grantor !== account || consent.grant.grantee !== this.host.accountId) reject('invalid_binding', 'Consent differs from envelope participants.');
            authorizedDevice(consent.deviceState, entry.envelope.fromDeviceId, this.host.context, this.host.clock.nowSeconds());
            if (!this.#verified(consent.grant, consent.deviceState).signatures[entry.envelope.fromDeviceId]) reject('invalid_signature', 'Consent sender did not sign its grant.');
        });
        const guards = [...await this.#snapshot([contactKey(account), contactRequestKey(account, 'incoming'), contactRequestKey(account, 'outgoing'), cachedDeviceStateKey(account)], signal)];
        const previous = readContact(guards[0]!.value); const waiting = readContactRequest(guards[2]!.value); const cached = state(guards[3]!.value);
        const waitingDelivery = waiting ? (await this.host.store.read([outboxKey(waiting.messageId)], signal)).sets[0]![0] : undefined;
        if (waiting) guards.push({ query: outboxKey(waiting.messageId), value: waitingDelivery?.value });
        const accepted = waiting?.sendState === 'targetAccepted' || waitingDelivery !== undefined && decodeOutbox(waitingDelivery).acceptedAt !== undefined;
        if (cached && (cached.revision > consent.deviceState.revision || cached.revision === consent.deviceState.revision && accountDeviceStateCodec.stringify(cached) !== accountDeviceStateCodec.stringify(consent.deviceState))) reject('stale_state', 'Consent contains an older or conflicting device state.');
        const mutations: StoreMutation[] = [put(cachedDeviceStateKey(account), accountDeviceStateCodec.encode(consent.deviceState))];
        if (previous?.record.status === 'active' || waiting && waiting.sendState !== 'canceled') {
            const grant = pureValidation(() => this.#merge(previous?.record.grantFromContact, consent.grant, consent.deviceState));
            const outgoing = waiting?.consent.grant ?? previous?.record.grantToContact;
            const record: ContactRecord = { ...(previous?.record ?? { account, status: 'active' as const, updatedAt: 0 }), status: 'active', updatedAt: this.#nextTime(previous?.record.updatedAt), grantFromContact: grant, ...(outgoing ? { grantToContact: outgoing } : {}) };
            const current: StoredContact = { ...previous, record, ...(waiting && accepted && outgoing ? { confirmedGrantTo: outgoing } : {}) };
            const sync = await this.#sync(current, signal); mutations.push(put(contactKey(account), encodeContact(current)), ...removeRequests(account));
            return this.#effect(guards, mutations, [sync]);
        }
        const request: StoredContactRequest = { accountId: account, direction: 'incoming', messageId: entry.envelope.messageId, createdAt: entry.envelope.createdAt, consent, ...(consent.note === undefined ? {} : { note: consent.note }) };
        mutations.push(put(contactRequestKey(account, 'incoming'), encodeContactRequest(request))); return this.#effect(guards, mutations);
    }
    async #receiveSync(entry: MessageTimelineEntry, sync: AccountContactSync, signal?: AbortSignal): Promise<MessageEffects> {
        const own = await this.host.getState(this.host.accountId, signal); pureValidation(() => authorizedDevice(own, entry.envelope.fromDeviceId, this.host.context, this.host.clock.nowSeconds()));
        const guards = await this.#snapshot((sync.records ?? []).map(value => contactKey(value.account)), signal); const mutations: StoreMutation[] = []; const prepared: PreparedContactSend[] = [];
        for (let index = 0; index < (sync.records?.length ?? 0); index++) {
            const source = sync.records![index]!; const previous = readContact(guards[index]!.value);
            if (previous && previous.record.updatedAt > source.updatedAt) continue;
            let incoming: ContactGrant | undefined; let outgoing: ContactGrant | undefined; let added = false;
            if (source.grantFromContact) { const remote = await this.host.getState(source.grantFromContact, signal); incoming = pureValidation(() => this.#merge(previous?.record.grantFromContact, source.grantFromContact!, remote)); }
            if (source.grantToContact) { const verified = pureValidation(() => this.#verified(source.grantToContact!, own)); const supplemented = await this.#signGrant(verified, signal);
                outgoing = pureValidation(() => this.#merge(previous?.record.grantToContact, supplemented, own)); added = contactGrantCodec.stringify(supplemented) !== contactGrantCodec.stringify(verified); }
            const current: StoredContact = source.status === 'deleted' ? { record: source } : { ...previous, record: { ...source,
                ...(incoming ?? previous?.record.grantFromContact ? { grantFromContact: incoming ?? previous!.record.grantFromContact! } : {}),
                ...(outgoing ?? previous?.record.grantToContact ? { grantToContact: outgoing ?? previous!.record.grantToContact! } : {}), ...(added ? { updatedAt: this.#nextTime(source.updatedAt) } : {}) } };
            mutations.push(put(contactKey(source.account), encodeContact(current))); if (source.status === 'deleted') mutations.push(...removeRequests(source.account));
            if (added) { prepared.push(await this.#sync(current, signal)); if (current.record.grantFromContact && outgoing) prepared.push(await this.host.prepareSend(source.account, contactGrantCodec.encode(outgoing), current.record.grantFromContact, undefined, signal)); }
        }
        if (sync.requestSnapshot) {
            const rows = await this.host.store.read([{ collection: 'contacts' }], signal);
            const records = new Map(rows.sets[0]!.map(row => [row.key, readContact(row.value)!]));
            for (const mutation of mutations) if (mutation.kind === 'put' && mutation.collection === 'contacts') records.set(mutation.key, readContact(mutation.value)!);
            let batch: ContactRecord[] = []; let size = 0;
            const flush = async (): Promise<void> => { if (!batch.length) return; prepared.push(await this.host.prepareSend(this.host.accountId, accountContactSyncCodec.encode({ records: batch }), undefined, [entry.envelope.fromDeviceId], signal)); batch = []; size = 0; };
            for (const record of records.values()) {
                const item = this.#protocolRecord(record); const bytes = encodeUtf8(contactRecordCodec.stringify(item)).length;
                if (bytes > 131072) throw new ProtocolError('invalid_size', 'A contact record exceeds synchronization limits.');
                if (batch.length >= 128 || batch.length && size + bytes > 131072) await flush(); batch.push(item); size += bytes;
            }
            await flush();
        }
        return this.#authorize(entry, this.#effect(guards, mutations, prepared, ['relationship', 'alias', 'authorization']));
    }
    deliveryEffects(grants: ReadonlyMap<string, ContactGrant>): OutboxEffects {
        return { queries: [{ collection: 'contact_requests' }, { collection: 'contacts' }, cachedDeviceStateKey(this.host.accountId)],
            committed: (snapshot, mutations) => { if (mutations.length) this.host.changed(this.#changes(snapshot, mutations, [])); }, plan: (snapshot, change) => {
            const record = change.current; const envelope = record.request.envelope; if (record.isDirect || envelope.to === this.host.accountId) return [];
            const mutations: StoreMutation[] = []; const key = contactRequestKey(envelope.to, 'outgoing');
            const requestRow = snapshot.sets[1]!.find(row => row.key === key.key); const request = readContactRequest(requestRow?.value);
            if (request?.messageId === envelope.messageId) mutations.push(record.state === 'canceled' ? { kind: 'delete', ...key } : put(key, encodeContactRequest({ ...request, sendState: record.state })));
            const contact = readContact(snapshot.sets[2]!.find(row => row.key === envelope.to)?.value); const own = state(snapshot.sets[3]![0]?.value); const grant = grants.get(envelope.messageId);
            if (record.acceptedAt !== undefined && contact?.record.status === 'active' && own && grant)
                mutations.push(put(contactKey(envelope.to), encodeContact({ ...contact, confirmedGrantTo: this.#merge(contact.confirmedGrantTo, grant, own) })));
            return mutations;
        } };
    }
    /** Repairs local grant signatures and acknowledges peer device-state revisions with durable outgoing messages. */
    async maintain(account: string, notifyDeviceRevision: number | undefined, signal?: AbortSignal): Promise<void> {
        const guards = await this.#snapshot([contactKey(account)], signal); const saved = readContact(guards[0]!.value);
        if (!saved || saved.record.status !== 'active') return;
        const now = this.host.clock.nowSeconds(); const own = await this.host.getState(this.host.accountId, signal);
        authorizedDevice(own, certificateId(this.host.device.certificate, this.host.context), this.host.context, now);
        const incoming = saved.record.grantFromContact && (saved.record.grantFromContact.expiresAt ?? Infinity) > now ? saved.record.grantFromContact : undefined;
        let revisionResolved = false;
        if (saved.requiredDeviceRevision !== undefined && incoming) {
            const remote = await this.host.getState(incoming, signal);
            if (remote.revision < saved.requiredDeviceRevision) throw new ProtocolError('stale_device_state', 'Contact device state has not reached its announced revision.'); revisionResolved = true;
        }
        const issued = saved.record.grantToContact; let supplemented: ContactGrant | undefined;
        if (issued && (issued.expiresAt ?? Infinity) > now) supplemented = await this.#signGrant(filterContactGrantSignatures(issued, own, this.host.context, now), signal);
        const changedGrant = supplemented !== undefined && contactGrantCodec.stringify(supplemented) !== contactGrantCodec.stringify(issued!);
        const { requiredDeviceRevision, ...withoutRevision } = saved;
        const current: StoredContact = { ...(revisionResolved ? withoutRevision : saved), record: { ...saved.record, ...(changedGrant ? { grantToContact: supplemented!, updatedAt: this.#nextTime(saved.record.updatedAt) } : {}) } };
        const prepared: PreparedContactSend[] = [];
        if (changedGrant) { prepared.push(await this.#sync(current, signal)); if (incoming) prepared.push(await this.host.prepareSend(account, contactGrantCodec.encode(supplemented!), incoming, undefined, signal)); }
        if (notifyDeviceRevision !== undefined && incoming) prepared.push(await this.host.prepareSend(account, deviceStateChangedCodec.encode({ revision: own.revision }), incoming, undefined, signal));
        const mutations = changedGrant || revisionResolved ? [put(contactKey(account), encodeContact(current))] : [];
        if (mutations.length || prepared.length) await this.#commit(this.#effect(guards, mutations, prepared, ['authorization']), signal);
    }
}
function state(value: JsonObject | undefined): AccountDeviceState | undefined { return value && accountDeviceStateCodec.decode(value); }
function ordinal(a: string, b: string): number { return a < b ? -1 : a > b ? 1 : 0; }
