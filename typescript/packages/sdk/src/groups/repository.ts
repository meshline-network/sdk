import { ProtocolError, StateConflictError } from '../errors.js';
import { groupManagementHash } from '../crypto/groups.js';
import { validateAccountId } from '../identity/neo.js';
import { contentHashBytes } from '../models/content.js';
import { groupEventCodec, groupMemberCodec, groupStateCodec, groupSyncPageCodec, validateGroupState, validateGroupSyncPage, type GroupEvent, type GroupSequenceQuery, type GroupSyncPage } from '../models/group-management.js';
import { groupKeyEntryCodec, groupKeyPageCodec, validateGroupKeyPage, type GroupKeyEntry, type GroupKeyPage } from '../models/group-keys.js';
import { validateGroupRef, type GroupRef } from '../models/groups.js';
import { certificateId, deviceCertificateCodec } from '../models/identity.js';
import type { NetworkContext } from '../protocol/context.js';
import { decodeBase64Url, encodeBase64Url, equalBytes } from '../protocol/encoding.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { requireLength } from '../crypto/primitives.js';
import type { MeshlineStore, RecordKey, StoreMutation, StoredRecord } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { applyGroupEvent, groupChangeKinds, type GroupChangeKind, type GroupDeparture, type GroupEventResult, type GroupProjectedMember, type GroupProjection } from './state.js';

export interface GroupEpochRecord { readonly group: GroupRef; readonly epoch: number; readonly commitment: string; readonly memberPublicKey?: Uint8Array; readonly keyEntry?: GroupKeyEntry }
export interface GroupPageResult { readonly projection?: GroupProjection; readonly hasMore: boolean; readonly rejected: readonly { readonly sequence: number; readonly error: ProtocolError }[]; readonly messageSequences: readonly number[];
    readonly change?: { readonly record: JsonObject; readonly kinds: readonly GroupChangeKind[] } }
export interface GroupTimelineSource { read(query: GroupSequenceQuery, signal?: AbortSignal): Promise<GroupSyncPage> }
const index = (value: number): string => { requireSafeInteger(value, 0); return String(value).padStart(16, '0'); };
export const groupKey = (group: GroupRef): RecordKey => ({ collection: 'groups', key: group.groupId });
export const groupEventKey = (group: GroupRef, sequence: number): RecordKey => ({ collection: 'group_events', key: `${group.groupId}|${index(sequence)}` });
export const groupEpochKey = (group: GroupRef, epoch: number): RecordKey => ({ collection: 'group_epochs', key: `${group.groupId}|${index(epoch)}` });
export const groupPendingMessageKey = (group: GroupRef, sequence: number): RecordKey => ({ collection: 'group_pending_messages', key: `${group.groupId}|${index(sequence)}` });
export const groupAcceptanceKey = (group: GroupRef, payload: JsonObject, context: NetworkContext): RecordKey => ({ collection: 'group_acceptances', key: `${group.groupId}|${groupManagementHash(payload, context)}` });
const messageKey = (group: GroupRef, sender: string, messageId: string): RecordKey => ({ collection: 'group_message_ids', key: `${group.groupId}|${sender}|${messageId}` });
const put = (key: RecordKey, value: JsonObject): StoreMutation => ({ ...key, kind: 'put', value });
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
export function encodeGroupProjection(value: GroupProjection): JsonObject {
    return { sequence: value.sequence, epoch: value.epoch, managementHash: value.managementHash, clientSecretCommitment: value.clientSecretCommitment, state: groupStateCodec.encode(value.state),
        members: value.members.map(member => ({ ...groupMemberCodec.encode({ account: member.account, role: member.role, memberEncryptionPublicKey: member.memberEncryptionPublicKey }), joinedAtSequence: member.joinedAtSequence,
            ...(member.nickname === undefined ? {} : { nickname: member.nickname }), ...(member.nicknameSequence === undefined ? {} : { nicknameSequence: member.nicknameSequence }) })), bans: [...value.bans], departures: { ...value.departures } };
}
/** Persisted projections are validated as local state, never accepted from a relay preview. */
export function decodeGroupProjection(value: JsonObject, group: GroupRef): GroupProjection {
    validateGroupRef(group); requireSafeInteger(value.sequence, 0); requireSafeInteger(value.epoch, 0); const sequence = value.sequence; const state = groupStateCodec.decode(value.state!); validateGroupState(state);
    if (state.groupId !== group.groupId || typeof value.managementHash !== 'string' || typeof value.clientSecretCommitment !== 'string' || !Array.isArray(value.members) || !Array.isArray(value.bans)) throw new ProtocolError('invalid_storage', 'Malformed verified group state.');
    contentHashBytes(value.managementHash); contentHashBytes(value.clientSecretCommitment); const seen = new Set<string>();
    const members = value.members.map(item => {
        const wire = { ...requireObject(item) }; const joinedAtSequence = wire.joinedAtSequence; const nickname = wire.nickname; const nicknameSequence = wire.nicknameSequence;
        delete wire.joinedAtSequence; delete wire.nickname; delete wire.nicknameSequence;
        const member = groupMemberCodec.decode(wire); validateAccountId(member.account); requireLength(member.memberEncryptionPublicKey, 32, 'Stored member public key'); requireSafeInteger(joinedAtSequence, 0);
        if (joinedAtSequence > sequence || seen.has(member.account)) throw new ProtocolError('invalid_storage', 'Stored group member has an invalid join sequence or duplicate account.'); seen.add(member.account);
        if (nickname !== undefined && typeof nickname !== 'string') throw new ProtocolError('invalid_storage', 'Stored nickname must be a string.');
        if (nicknameSequence !== undefined) { requireSafeInteger(nicknameSequence, joinedAtSequence); if (nicknameSequence > sequence) throw new ProtocolError('invalid_storage', 'Stored nickname sequence exceeds group position.'); }
        return { ...member, joinedAtSequence, ...(nickname === undefined ? {} : { nickname }), ...(nicknameSequence === undefined ? {} : { nicknameSequence }) } as GroupProjectedMember;
    });
    const bans = value.bans.map(item => { if (typeof item !== 'string') throw new ProtocolError('invalid_storage', 'Stored ban must name an account.'); validateAccountId(item); return item; });
    if (new Set(bans).size !== bans.length || bans.some(account => seen.has(account)) || members.length !== state.memberCount || members.filter(member => member.role === 'owner').length !== 1 || members.find(member => member.role === 'owner')!.account !== state.owner) throw new ProtocolError('invalid_storage', 'Stored group membership, owner or bans are inconsistent.');
    const departures: Record<string, GroupDeparture> = Object.create(null) as Record<string, GroupDeparture>;
    for (const [account, departure] of Object.entries(requireObject(value.departures!))) {
        validateAccountId(account); if (typeof departure !== 'string' || !['left', 'removed', 'banned', 'not_member'].includes(departure) || seen.has(account)) throw new ProtocolError('invalid_storage', 'Invalid stored departure.'); departures[account] = departure as GroupDeparture;
    }
    return { ...group, sequence: value.sequence, epoch: value.epoch, managementHash: value.managementHash, clientSecretCommitment: value.clientSecretCommitment, state, members, bans, departures };
}
function recordProjection(value: JsonObject | undefined, group: GroupRef): GroupProjection | undefined {
    if (!value) return undefined; if (value.relayId !== group.relayId) throw new ProtocolError('invalid_binding', 'Stored group belongs to another hosting relay.');
    return value.projection === undefined ? undefined : decodeGroupProjection(requireObject(value.projection), group);
}
function decodeEpoch(row: StoredRecord, group: GroupRef): GroupEpochRecord {
    const value = row.value; requireSafeInteger(value.epoch, 0);
    if (row.key !== groupEpochKey(group, value.epoch).key || typeof value.commitment !== 'string') throw new ProtocolError('invalid_storage', 'Stored group epoch has an invalid identity or commitment.'); contentHashBytes(value.commitment);
    const memberPublicKey = value.memberPublicKey === undefined ? undefined : decodeBase64Url(String(value.memberPublicKey)); if (memberPublicKey) requireLength(memberPublicKey, 32, 'Epoch member public key');
    const keyEntry = value.keyEntry === undefined ? undefined : groupKeyEntryCodec.decode(value.keyEntry);
    if (keyEntry && keyEntry.epoch !== value.epoch) throw new ProtocolError('invalid_storage', 'Stored group key entry belongs to another epoch.');
    return { group, epoch: value.epoch, commitment: value.commitment, ...(memberPublicKey ? { memberPublicKey } : {}), ...(keyEntry ? { keyEntry } : {}) };
}

/** Commits verified group state, epoch authority, signed evidence, deduplication and cursor in one CAS batch. */
export class GroupRepository {
    constructor(readonly store: MeshlineStore, readonly context: NetworkContext, readonly accountId: string) { validateAccountId(accountId); }
    async get(group: GroupRef, signal?: AbortSignal): Promise<GroupProjection | undefined> { validateGroupRef(group); return recordProjection((await this.store.read([groupKey(group)], signal)).sets[0]![0]?.value, group); }
    /** Pre-admission relay previews are presentation data and never replace verified history or establish epoch authority. */
    async savePreview(group: GroupRef, source: ReturnType<typeof groupStateCodec.decode>, signal?: AbortSignal): Promise<void> {
        group = { ...group }; validateGroupRef(group); const state = groupStateCodec.decode(groupStateCodec.encode(source)); validateGroupState(state);
        if (state.groupId !== group.groupId) throw new ProtocolError('invalid_binding', 'Relay preview belongs to another group.');
        await updateStore(this.store, [groupKey(group)], snapshot => {
            const current = snapshot.sets[0]![0]?.value; recordProjection(current, group);
            return { mutations: current?.projection ? [] : [put(groupKey(group), { ...current, relayId: group.relayId, preview: groupStateCodec.encode(state) })], result: undefined };
        }, signal);
    }
    async event(group: GroupRef, sequence: number, signal?: AbortSignal): Promise<JsonObject | undefined> {
        validateGroupRef(group); const snapshot = await this.store.read([groupKey(group), groupEventKey(group, sequence)], signal); recordProjection(snapshot.sets[0]![0]?.value, group); return snapshot.sets[1]![0]?.value;
    }
    async epochs(group: GroupRef, signal?: AbortSignal): Promise<readonly GroupEpochRecord[]> {
        validateGroupRef(group); const snapshot = await this.store.read([groupKey(group), { collection: 'group_epochs', prefix: `${group.groupId}|` }], signal); recordProjection(snapshot.sets[0]![0]?.value, group); return snapshot.sets[1]!.map(row => decodeEpoch(row, group));
    }
    async keyProgress(group: GroupRef, signal?: AbortSignal): Promise<number> { const values = await this.epochs(group, signal); return values.reduce((after, entry) => entry.keyEntry ? Math.max(after, entry.epoch) : after, -1); }
    async synchronizePage(group: GroupRef, source: GroupTimelineSource, signal?: AbortSignal): Promise<GroupPageResult> {
        validateGroupRef(group); const initial = (await this.store.read([groupKey(group)], signal)).sets[0]![0]?.value; const before = recordProjection(initial, group);
        const query = { groupId: group.groupId, after: before?.sequence ?? -1 };
        const page = groupSyncPageCodec.decode(groupSyncPageCodec.encode(await source.read(query, signal))); validateGroupSyncPage(page, query, this.context);
        const certificates = new Map(page.certificates.map(certificate => [certificateId(certificate, this.context), certificate]));
        const verified: { entry: GroupEvent; result: GroupEventResult; epochChanged: boolean }[] = []; let projection = before;
        for (const entry of page.events) {
            const result = applyGroupEvent(projection, entry, entry.signerDeviceId === undefined ? undefined : certificates.get(entry.signerDeviceId), group, this.context);
            verified.push({ entry, result, epochChanged: projection?.epoch !== result.projection.epoch }); projection = result.projection;
        }
        const keys = new Map<string, RecordKey>(); const addKey = (key: RecordKey): void => { keys.set(`${key.collection}|${key.key}`, key); };
        addKey(groupKey(group));
        for (const item of verified) {
            addKey(groupEventKey(group, item.entry.sequence)); if (item.epochChanged) addKey(groupEpochKey(group, item.entry.epoch));
            const message = item.result.message; if (message) addKey(messageKey(group, message.sender.account, message.envelope.messageId));
        }
        const queries = [...keys.values()];
        return updateStore(this.store, queries, snapshot => {
            const rows = new Map(queries.map((key, i) => [`${key.collection}|${key.key}`, snapshot.sets[i]![0]]));
            const row = (key: RecordKey) => rows.get(`${key.collection}|${key.key}`);
            if (fingerprint(row(groupKey(group))?.value) !== fingerprint(initial)) throw new StateConflictError('Group state changed while its timeline was being verified.');
            const mutations: StoreMutation[] = []; const rejected: { sequence: number; error: ProtocolError }[] = []; const messageSequences: number[] = []; const newMessages = new Set<string>();
            for (const { entry, result, epochChanged } of verified) {
                const key = groupEventKey(group, entry.sequence); if (row(key)) throw new ProtocolError('conflicting_event', 'Group event already exists beyond the verified cursor.');
                let rejection = result.rejection; const message = result.message; const identity = message && messageKey(group, message.sender.account, message.envelope.messageId);
                if (identity && (row(identity) || newMessages.has(identity.key))) rejection = new ProtocolError('duplicate_message', 'Relay assigned a second sequence to an existing logical group message.');
                const certificate = entry.signerDeviceId === undefined ? undefined : certificates.get(entry.signerDeviceId)!;
                const value: JsonObject = { event: groupEventCodec.encode(entry), ...(certificate ? { certificate: deviceCertificateCodec.encode(certificate) } : {}) };
                if (rejection) { value.rejection = { code: rejection.code, message: rejection.message }; rejected.push({ sequence: entry.sequence, error: rejection }); }
                else if (message && identity) {
                    value.message = { sender: message.sender.account, messageId: message.envelope.messageId, senderDeviceId: message.signerDeviceId, createdAt: message.envelope.createdAt, joinedAtSequence: message.joinedAtSequence };
                    newMessages.add(identity.key); mutations.push(put(identity, { sequence: entry.sequence }), put(groupPendingMessageKey(group, entry.sequence), { sequence: entry.sequence })); messageSequences.push(entry.sequence);
                }
                mutations.push(put(key, value));
                if (!rejection && entry.signerDeviceId) mutations.push(put(groupAcceptanceKey(group, entry.payload, this.context), { sequence: entry.sequence }));
                if (epochChanged) {
                    const epochKey = groupEpochKey(group, entry.epoch); const old = row(epochKey)?.value; const memberPublicKey = result.projection.members.find(member => member.account === this.accountId)?.memberEncryptionPublicKey;
                    if (old?.commitment !== undefined && (old.commitment !== result.projection.clientSecretCommitment || old.memberPublicKey !== (memberPublicKey && encodeBase64Url(memberPublicKey)))) throw new ProtocolError('conflicting_epoch', 'Stored group epoch authority conflicts with verified management history.');
                    mutations.push(put(epochKey, { ...old, epoch: entry.epoch, commitment: result.projection.clientSecretCommitment, ...(memberPublicKey ? { memberPublicKey: encodeBase64Url(memberPublicKey) } : {}) }));
                }
            }
            let change: GroupPageResult['change'];
            if (verified.length) {
                const ownerSequence = verified.filter(item => item.entry.payload.$type === 'meshline.group.owner.transfer').at(-1)?.entry.sequence ?? initial?.ownerSequence ?? 0;
                const next: JsonObject = { ...initial, relayId: group.relayId, projection: encodeGroupProjection(projection!), ownerSequence };
                if (projection!.members.some(member => member.account === this.accountId)) delete next.pendingApplication;
                mutations.push(put(groupKey(group), next));
                const kinds = groupChangeKinds.filter(kind => verified.some(item => item.result.changeKinds?.includes(kind)));
                if (kinds.length) change = { record: next, kinds };
            }
            return { mutations, result: { ...(projection ? { projection } : {}), ...(change ? { change } : {}), hasMore: page.hasMore, rejected, messageSequences } };
        }, signal);
    }
    /** Reuses an omitted client box only inside this page and only while both verified commitment and account key remain unchanged. */
    async saveKeyPage(group: GroupRef, source: GroupKeyPage, after: number, signal?: AbortSignal): Promise<void> {
        validateGroupRef(group); const page = groupKeyPageCodec.decode(groupKeyPageCodec.encode(source)); validateGroupKeyPage(page, after);
        const keys = page.keys.map(entry => groupEpochKey(group, entry.epoch));
        await updateStore(this.store, [groupKey(group), ...keys], snapshot => {
            const projection = recordProjection(snapshot.sets[0]![0]?.value, group); const mutations: StoreMutation[] = []; let previous: GroupEpochRecord | undefined; let previousBox: GroupKeyEntry['clientSecretBox'];
            for (let i = 0; i < page.keys.length; i++) {
                const entry = page.keys[i]!; const row = snapshot.sets[i + 1]![0]; if (!projection || entry.epoch > projection.epoch || !row) throw new ProtocolError('unverified_epoch', 'Management history has not established this group key epoch.');
                const epoch = decodeEpoch(row, group); if (!epoch.memberPublicKey) throw new ProtocolError('unauthorized_epoch', 'Group key epoch was not accessible to this account.');
                const changed = !previous || previous.commitment !== epoch.commitment || !equalBytes(previous.memberPublicKey!, epoch.memberPublicKey);
                if (changed && !entry.clientSecretBox) throw new ProtocolError('missing_secret_box', 'A changed verified commitment or member key requires a client secret box.');
                const expanded = { ...entry, clientSecretBox: entry.clientSecretBox ?? previousBox! }; const wire = groupKeyEntryCodec.encode(expanded);
                // Wrapping randomness is not an epoch identity. Derivation must compare any previously verified application secret before replacing it.
                mutations.push(put(keys[i]!, { ...row.value, keyEntry: wire })); previous = epoch; previousBox = expanded.clientSecretBox;
            }
            return { mutations, result: undefined };
        }, signal);
    }
}
