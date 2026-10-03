import { ProtocolError, StateConflictError } from '../errors.js';
import { validateAccountId } from '../identity/neo.js';
import { groupApplicationCodec, groupCreateRequestCodec, groupEventCodec, groupInviteCodec, groupManagementPayloadCodec, groupMemberRecoveryRequestCodec, groupStateCodec, validateGroupApplication, validateGroupCreateRequest, validateGroupInvite, validateGroupManagementPayload, validateGroupMemberRecoveryRequest, validateGroupState, type GroupState } from '../models/group-management.js';
import { groupAccountsRequestCodec, groupApplicationApproveRequestCodec, groupInviteQueryCodec, groupRecoveryApproveRequestCodec, groupRecoverySubmitResultCodec, validateGroupAccountsRequest, validateGroupApplicationApproveRequest, validateGroupInviteQuery, validateGroupRecoveryApproveRequest, validateGroupRecoverySubmitResult } from '../models/group-admission.js';
import { groupMessageEnvelopeCodec, validateGroupMessageEnvelope, validateGroupRef, type GroupRef } from '../models/groups.js';
import type { NetworkContext } from '../protocol/context.js';
import { equalBytes } from '../protocol/encoding.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import type { MeshlineStore, RecordKey, StoreMutation } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import type { HttpMethod } from '../transport/http.js';
import { decodeGroupProjection, groupAcceptanceKey, groupEventKey, groupKey } from './repository.js';
import { groupMemberSecretKey, groupRotationKey, type PreparedGroupMemberKey } from './secrets.js';

export const groupOperationMethods = {
    'group.create': 'POST', 'group.update': 'PATCH', 'group.close': 'DELETE', 'group.message.send': 'POST',
    'group.member.leave': 'DELETE', 'group.member.remove': 'DELETE', 'group.member.ban': 'PUT', 'group.member.unban': 'DELETE',
    'group.role.update': 'PUT', 'group.owner.transfer': 'POST',
    'group.invite.create': 'POST', 'group.invite.revoke': 'DELETE',
    'group.application.submit': 'POST', 'group.application.approve': 'POST', 'group.application.reject': 'DELETE',
    'group.member.recovery.submit': 'POST', 'group.member.recovery.approve': 'POST', 'group.member.recovery.reject': 'DELETE',
    'group.secret.rotation.commit': 'POST',
} as const satisfies Record<string, HttpMethod>;
export type GroupOperationMethod = keyof typeof groupOperationMethods;
export interface GroupOperation { readonly group: GroupRef; readonly method: GroupOperationMethod; readonly request: JsonObject; readonly accepted?: true; readonly acceptedSequence?: number; readonly acceptedResult?: JsonObject }
const key = (operation: Pick<GroupOperation, 'group' | 'method'>): RecordKey => ({ collection: 'group_operations', key: `${operation.group.groupId}|${operation.method}` });
const put = (record: RecordKey, value: JsonObject): StoreMutation => ({ ...record, kind: 'put', value });
export function groupOperationPayload(operation: GroupOperation): JsonObject | undefined {
    validateGroupRef(operation.group); if (!Object.hasOwn(groupOperationMethods, operation.method)) throw new ProtocolError('invalid_operation', 'Unknown stored group operation.');
    let payload: JsonObject | undefined;
    if (operation.method === 'group.create') { const request = groupCreateRequestCodec.decode(operation.request); validateGroupCreateRequest(request); payload = requireObject(operation.request.create!); }
    else if (operation.method === 'group.invite.create') { const request = groupInviteCodec.decode(operation.request); validateGroupInvite(request, request.createdAt); }
    else if (operation.method === 'group.invite.revoke') validateGroupInviteQuery(groupInviteQueryCodec.decode(operation.request));
    else if (operation.method === 'group.application.submit') validateGroupApplication(groupApplicationCodec.decode(operation.request));
    else if (operation.method === 'group.member.recovery.submit') validateGroupMemberRecoveryRequest(groupMemberRecoveryRequestCodec.decode(operation.request));
    else if (operation.method === 'group.application.reject' || operation.method === 'group.member.recovery.reject') validateGroupAccountsRequest(groupAccountsRequestCodec.decode(operation.request));
    else if (operation.method === 'group.application.approve') { validateGroupApplicationApproveRequest(groupApplicationApproveRequestCodec.decode(operation.request)); payload = requireObject(operation.request.approval!); }
    else if (operation.method === 'group.member.recovery.approve') { validateGroupRecoveryApproveRequest(groupRecoveryApproveRequestCodec.decode(operation.request)); payload = requireObject(operation.request.approval!); }
    else if (operation.method === 'group.message.send') { validateGroupMessageEnvelope(groupMessageEnvelopeCodec.decode(operation.request)); payload = operation.request; }
    else {
        const value = groupManagementPayloadCodec.decode(operation.request); validateGroupManagementPayload(value);
        const expected: Partial<Record<GroupOperationMethod, typeof value.kind>> = { 'group.update': 'update', 'group.close': 'close', 'group.member.leave': 'memberLeave', 'group.member.remove': 'memberRemoval', 'group.member.ban': 'memberBan', 'group.member.unban': 'memberUnban', 'group.role.update': 'roleUpdate', 'group.owner.transfer': 'ownerTransfer', 'group.secret.rotation.commit': 'secretRotation' };
        if (value.kind !== expected[operation.method]) throw new ProtocolError('invalid_operation', 'Stored operation method and signed payload differ.'); payload = operation.request;
    }
    if ((payload ?? operation.request).group_id !== operation.group.groupId) throw new ProtocolError('invalid_binding', 'Stored operation belongs to another group.');
    if (operation.acceptedSequence !== undefined) { requireSafeInteger(operation.acceptedSequence, 1); if (operation.method !== 'group.message.send' || !operation.accepted) throw new ProtocolError('invalid_operation', 'Unexpected group operation sequence acknowledgement.'); }
    if (operation.acceptedResult !== undefined) {
        if (operation.method !== 'group.member.recovery.submit' || !operation.accepted) throw new ProtocolError('invalid_operation', 'Unexpected group operation acceptance result.');
        validateGroupRecoverySubmitResult(groupRecoverySubmitResultCodec.decode(operation.acceptedResult));
    }
    return payload;
}
function encode(operation: GroupOperation): JsonObject { return { groupId: operation.group.groupId, relayId: operation.group.relayId, method: operation.method, request: operation.request, ...(operation.accepted ? { accepted: true } : {}), ...(operation.acceptedSequence === undefined ? {} : { acceptedSequence: operation.acceptedSequence }), ...(operation.acceptedResult === undefined ? {} : { acceptedResult: operation.acceptedResult }) }; }
function decode(value: JsonObject): GroupOperation {
    if (typeof value.groupId !== 'string' || typeof value.relayId !== 'string' || typeof value.method !== 'string' || value.accepted !== undefined && value.accepted !== true) throw new ProtocolError('invalid_storage', 'Malformed stored group operation.');
    const operation: GroupOperation = { group: { groupId: value.groupId, relayId: value.relayId }, method: value.method as GroupOperationMethod, request: requireObject(value.request!), ...(value.accepted ? { accepted: true } : {}), ...(value.acceptedSequence === undefined ? {} : { acceptedSequence: value.acceptedSequence as number }), ...(value.acceptedResult === undefined ? {} : { acceptedResult: requireObject(value.acceptedResult) }) };
    groupOperationPayload(operation); return operation;
}
const same = (a: GroupOperation, b: GroupOperation): boolean => a.group.relayId === b.group.relayId && canonicalJson(a.request) === canonicalJson(b.request);

/** Exact signed requests survive uncertain responses; positive receipts are durable before any follow-up I/O. */
export class GroupOperations {
    constructor(readonly store: MeshlineStore, readonly context: NetworkContext, readonly accountId: string) { validateAccountId(accountId); }
    async all(signal?: AbortSignal): Promise<readonly GroupOperation[]> { return (await this.store.read([{ collection: 'group_operations' }], signal)).sets[0]!.map(row => { const value = decode(row.value); if (key(value).key !== row.key) throw new ProtocolError('invalid_storage', 'Group operation key differs from its record.'); return value; }); }
    async ensureAvailable(group: GroupRef, method: GroupOperationMethod, signal?: AbortSignal): Promise<void> {
        if ((await this.store.read([key({ group, method })], signal)).sets[0]!.length) throw new StateConflictError('An earlier group operation is awaiting confirmation; start the manager to recover it.');
    }
    async save(operation: GroupOperation, member: PreparedGroupMemberKey | undefined, signal?: AbortSignal, preview?: GroupState): Promise<void> {
        operation = decode(encode(operation)); const queries = [key(operation), groupKey(operation.group)];
        const previewWire = preview === undefined ? undefined : groupStateCodec.encode(preview);
        if (preview) { validateGroupState(preview); if (preview.groupId !== operation.group.groupId) throw new ProtocolError('invalid_binding', 'Group preview belongs to another group.'); }
        if (member) { if (member.group.groupId !== operation.group.groupId || member.group.relayId !== operation.group.relayId) throw new ProtocolError('invalid_binding', 'Prepared member key belongs to another group.'); queries.push(groupMemberSecretKey(member.group.groupId, member.publicKey)); }
        await updateStore(this.store, queries, snapshot => {
            if (snapshot.sets[0]!.length) throw new StateConflictError('An earlier group operation is awaiting confirmation.');
            const group = snapshot.sets[1]![0]?.value;
            if (group && group.relayId !== operation.group.relayId) throw new ProtocolError('invalid_binding', 'Stored group belongs to another hosting relay.');
            const mutations: StoreMutation[] = [put(key(operation), encode(operation))];
            if (!group || previewWire && !group.projection) mutations.push(put(groupKey(operation.group), { ...group, relayId: operation.group.relayId, ...(previewWire ? { preview: previewWire } : {}) }));
            if (member) {
                if (snapshot.sets[2]!.length) throw new StateConflictError('Prepared member key already exists.');
                mutations.push(put(queries[2]!, { publicKey: queries[2]!.key.split('|')[1]!, protectedKey: member.protectedKey, shared: false }));
            }
            return { mutations, result: undefined };
        }, signal);
    }
    async acknowledge(operation: GroupOperation, acceptedSequence?: number, acceptedResult?: JsonObject): Promise<GroupOperation> {
        if (acceptedSequence !== undefined) requireSafeInteger(acceptedSequence, 1);
        return updateStore(this.store, [key(operation)], snapshot => {
            const row = snapshot.sets[0]![0]; if (!row) throw new StateConflictError('Group operation disappeared before its acceptance was recorded.'); const current = decode(row.value);
            if (!same(current, operation) || current.acceptedSequence !== undefined && current.acceptedSequence !== acceptedSequence || current.acceptedResult !== undefined && canonicalJson(current.acceptedResult) !== canonicalJson(acceptedResult ?? null)) throw new StateConflictError('Group operation acknowledgement conflicts with the pending request.');
            const result: GroupOperation = { ...current, accepted: true, ...(acceptedSequence === undefined ? {} : { acceptedSequence }), ...(acceptedResult === undefined ? {} : { acceptedResult }) }; groupOperationPayload(result);
            return { mutations: [put(key(operation), encode(result))], result };
        });
    }
    async acceptedEvent(operation: GroupOperation, signal?: AbortSignal): Promise<{ sequence: number; record: JsonObject } | undefined> {
        const payload = groupOperationPayload(operation); if (!payload) return undefined;
        const row = (await this.store.read([groupAcceptanceKey(operation.group, payload, this.context)], signal)).sets[0]![0]; if (!row) return undefined;
        requireSafeInteger(row.value.sequence, 0); const sequence = row.value.sequence;
        const record = (await this.store.read([groupEventKey(operation.group, sequence)], signal)).sets[0]![0]?.value;
        if (!record || canonicalJson(groupEventCodec.decode(record.event!).payload) !== canonicalJson(payload)) throw new ProtocolError('invalid_storage', 'Group acceptance evidence differs from its indexed signed payload.');
        if (operation.acceptedSequence !== undefined && operation.acceptedSequence !== sequence) throw new ProtocolError('conflicting_acknowledgement', 'Relay acknowledgement and verified group message sequence differ.');
        return { sequence, record };
    }
    /** Approval can outlive a lost or malformed submission acknowledgement. */
    async acceptedMemberRequest(operation: GroupOperation, signal?: AbortSignal): Promise<boolean> {
        const recovery = operation.method === 'group.member.recovery.submit';
        if (!recovery && operation.method !== 'group.application.submit') return false;
        const request = recovery ? groupMemberRecoveryRequestCodec.decode(operation.request) : groupApplicationCodec.decode(operation.request);
        if (request.account !== this.accountId) throw new ProtocolError('invalid_binding', 'Pending member request belongs to another account.');
        const snapshot = await this.store.read([groupKey(operation.group), { collection: 'group_events', prefix: `${operation.group.groupId}|` }], signal);
        const group = snapshot.sets[0]![0]?.value;
        if (!group?.projection) return false;
        if (group.relayId !== operation.group.relayId) throw new ProtocolError('invalid_binding', 'Pending member request belongs to another hosting relay.');
        const projection = decodeGroupProjection(requireObject(group.projection), operation.group);
        for (const row of snapshot.sets[1]!) {
            if (row.value.rejection !== undefined) continue;
            const event = groupEventCodec.decode(row.value.event!);
            if (event.payload.$type !== (recovery ? 'meshline.group.member.recovery.approval' : 'meshline.group.application.approval')) continue;
            const approval = groupManagementPayloadCodec.decode(event.payload);
            if (approval.kind !== 'applicationApproval' && approval.kind !== 'memberRecoveryApproval') continue;
            if (approval.value.groupId !== operation.group.groupId
                || !approval.value.members.some(member => member.account === request.account && equalBytes(member.memberEncryptionPublicKey, request.memberEncryptionPublicKey))) continue;
            const index = (await this.store.read([groupAcceptanceKey(operation.group, event.payload, this.context)], signal)).sets[0]![0];
            if (!event.signerDeviceId || event.sequence > projection.sequence || row.key !== groupEventKey(operation.group, event.sequence).key || index?.value.sequence !== event.sequence) throw new ProtocolError('invalid_storage', 'Member request approval is not indexed in verified group history.');
            return true;
        }
        return false;
    }
    /** The caller supplies verified evidence or a positive acknowledgement. Rejections never erase an earlier uncertain operation. */
    async complete(operation: GroupOperation, localEffect = false): Promise<JsonObject | undefined> {
        return updateStore(this.store, [key(operation), groupKey(operation.group), groupRotationKey(operation.group)], snapshot => {
            const row = snapshot.sets[0]![0]; if (!row) return { mutations: [], result: undefined };
            if (!same(decode(row.value), operation)) throw new StateConflictError('Pending group request changed before completion.');
            const mutations: StoreMutation[] = [{ kind: 'delete', ...key(operation) }]; const group = snapshot.sets[1]![0]?.value;
            if (localEffect && group) {
                if (operation.method === 'group.close') mutations.push(put(groupKey(operation.group), { ...group, locallyClosed: true }));
                if (operation.method === 'group.member.leave') mutations.push(put(groupKey(operation.group), { ...group, localDepartureAfter: requireObject(group.projection!).sequence! }));
                if (operation.method === 'group.application.submit' && (!group.projection || !decodeGroupProjection(requireObject(group.projection), operation.group).members.some(member => member.account === this.accountId))) mutations.push(put(groupKey(operation.group), { ...group, pendingApplication: true }));
                if (operation.method === 'group.secret.rotation.commit' && snapshot.sets[2]![0]?.value.commitment === operation.request.client_secret_commitment) mutations.push({ kind: 'delete', ...groupRotationKey(operation.group) });
            }
            const change = mutations.find(mutation => mutation.collection === 'groups' && mutation.kind === 'put');
            return { mutations, result: change?.kind === 'put' ? change.value : undefined };
        });
    }
}
