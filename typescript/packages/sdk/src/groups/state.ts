import { groupManagementHash } from '../crypto/groups.js';
import { ProtocolError } from '../errors.js';
import { deriveResourceId } from '../identity/identifiers.js';
import { groupEventCodec, groupManagementPayloadCodec, validateGroupEvent, verifyGroupManagementPayload,
    type GroupEvent, type GroupManagementPayload, type GroupMember, type GroupState } from '../models/group-management.js';
import { groupMessageEnvelopeCodec, validateGroupRef, verifyGroupMessageEnvelope, type GroupMemberKey, type GroupMessageEnvelope, type GroupRef } from '../models/groups.js';
import { certificateId, validateCertificate, type DeviceCertificate } from '../models/identity.js';
import type { NetworkContext } from '../protocol/context.js';
import { equalBytes } from '../protocol/encoding.js';
import { requireObject } from '../protocol/json.js';

export interface GroupProjectedMember extends GroupMember { readonly joinedAtSequence: number; readonly nickname?: string; readonly nicknameSequence?: number }
export type GroupDeparture = 'left' | 'removed' | 'banned' | 'not_member';
/** Local verified state. Relay previews must never be deserialized into this type. */
export interface GroupProjection extends GroupRef {
    readonly sequence: number; readonly epoch: number; readonly managementHash: string; readonly clientSecretCommitment: string;
    readonly state: GroupState; readonly members: readonly GroupProjectedMember[]; readonly bans: readonly string[]; readonly departures: Readonly<Record<string, GroupDeparture>>;
}
export interface VerifiedGroupMessage { readonly envelope: GroupMessageEnvelope; readonly sender: DeviceCertificate; readonly signerDeviceId: string; readonly joinedAtSequence: number }
export type GroupChangeKind = 'properties' | 'members' | 'roles' | 'bans' | 'nickname' | 'status';
export const groupChangeKinds: readonly GroupChangeKind[] = ['properties', 'members', 'roles', 'bans', 'nickname', 'status'];
const managementChanges: Readonly<Record<GroupManagementPayload['kind'], readonly GroupChangeKind[]>> = {
    create: ['properties', 'members', 'roles', 'status'], update: ['properties'], applicationApproval: ['members'], roleUpdate: ['roles'], ownerTransfer: ['roles'],
    memberLeave: ['members'], memberRemoval: ['members'], memberBan: ['members', 'bans'], memberUnban: ['bans'], memberRecoveryApproval: ['members'], secretRotation: ['members'], close: ['status'],
};
export interface GroupEventResult { readonly projection: GroupProjection; readonly message?: VerifiedGroupMessage; readonly rejection?: ProtocolError; readonly changeKinds?: readonly GroupChangeKind[] }

/** Applies one timeline event without mutating the preceding projection. Invalid administration stops the chain; invalid ordinary messages retain an explicit rejection. */
export function applyGroupEvent(previous: GroupProjection | undefined, source: GroupEvent, certificate: DeviceCertificate | undefined, group: GroupRef, context: NetworkContext): GroupEventResult {
    validateGroupRef(group); const entry = groupEventCodec.decode(groupEventCodec.encode(source)); validateGroupEvent(entry);
    if (previous && (previous.groupId !== group.groupId || previous.relayId !== group.relayId)) throw new ProtocolError('invalid_binding', 'Verified group state belongs to another group or relay.');
    if (entry.sequence <= (previous?.sequence ?? -1)) throw new ProtocolError('invalid_sequence', 'A group event must advance the verified sequence.');
    if (previous?.state.status === 'closed') throw new ProtocolError('group_closed', 'A closed group cannot append events.');
    if (entry.signerDeviceId !== undefined) {
        if (!certificate) throw new ProtocolError('missing_certificate', 'Group event signing certificate is missing.');
        validateCertificate(certificate, context); if (certificateId(certificate, context) !== entry.signerDeviceId) throw new ProtocolError('invalid_identity', 'Group event signing certificate belongs to another device.');
    }
    if (entry.payload.$type === 'meshline.group.key.rotated') {
        if (!previous || certificate !== undefined || entry.epoch <= previous.epoch) throw new ProtocolError('invalid_epoch', 'Relay key rotation requires an established group and a newer epoch.');
        return { projection: { ...previous, sequence: entry.sequence, epoch: entry.epoch } };
    }
    if (entry.payload.$type === 'meshline.group.message') {
        if (!previous || entry.epoch !== previous.epoch) throw new ProtocolError('invalid_epoch', 'Group message must use the verified current epoch.');
        const projection = { ...previous, sequence: entry.sequence };
        try {
            const member = previous.members.find(value => value.account === certificate!.account);
            if (!member || previous.bans.includes(certificate!.account)) throw new ProtocolError('unauthorized_member', 'Group message sender is not a current unbanned member.');
            const envelope = groupMessageEnvelopeCodec.decode(entry.payload);
            if (envelope.groupId !== group.groupId || envelope.epoch !== entry.epoch) throw new ProtocolError('invalid_binding', 'Group envelope and event have inconsistent group or epoch bindings.');
            verifyGroupMessageEnvelope(envelope, certificate!, entry.signerDeviceId!, context);
            return { projection, message: { envelope, sender: certificate!, signerDeviceId: entry.signerDeviceId!, joinedAtSequence: member.joinedAtSequence } };
        } catch (error) {
            if (!(error instanceof ProtocolError)) throw error;
            return { projection, rejection: error };
        }
    }
    const payload = groupManagementPayloadCodec.decode(entry.payload); verifyGroupManagementPayload(payload, certificate!, context);
    const actor = certificate!.account;
    if (payload.kind === 'create') {
        const value = payload.value;
        if (previous || entry.sequence !== 0 || entry.epoch !== 0 || value.owner.account !== actor || value.groupId !== group.groupId
            || deriveResourceId('group', actor, group.relayId, value.nonce, context) !== group.groupId) throw new ProtocolError('invalid_creation', 'Group creation does not establish the expected owner, group and hosting relay.');
        return { changeKinds: managementChanges.create, projection: { ...group, sequence: 0, epoch: 0, managementHash: groupManagementHash(requireObject(groupManagementPayloadCodec.encode(payload)), context), clientSecretCommitment: value.clientSecretCommitment,
            state: { groupId: group.groupId, name: value.name, ...(value.description === undefined ? {} : { description: value.description }), status: 'active', owner: actor, memberCapacity: value.memberCapacity, memberCount: 1, invitePolicy: value.invitePolicy },
            members: [{ account: actor, role: 'owner', memberEncryptionPublicKey: value.owner.memberEncryptionPublicKey.slice(), joinedAtSequence: 0 }], bans: [], departures: {} } };
    }
    if (!previous || payload.value.groupId !== group.groupId || payload.value.prevHash !== previous.managementHash) throw new ProtocolError('invalid_management_chain', 'Group management predecessor does not match the verified head.');
    const members = new Map<string, GroupProjectedMember>(previous.members.map(value => [value.account, { ...value, memberEncryptionPublicKey: value.memberEncryptionPublicKey.slice() }]));
    const bans = new Set(previous.bans); const departures = { ...previous.departures };
    const author = members.get(actor); if (!author || bans.has(actor)) throw new ProtocolError('unauthorized_member', 'Group administrator is not a current unbanned member.');
    const owner = author.role === 'owner'; const administrator = owner || author.role === 'administrator';
    let state = { ...previous.state }; let commitment = previous.clientSecretCommitment; let advances = false;
    const member = (account: string): GroupProjectedMember => { const value = members.get(account); if (!value) throw new ProtocolError('missing_member', 'Group management target is not a current member.'); return value; };
    const authorize = (allowed: boolean): void => { if (!allowed) throw new ProtocolError('unauthorized_role', 'Group actor is not authorized for this operation.'); };
    const add = (value: GroupMemberKey): void => { members.set(value.account, { account: value.account, role: 'member', memberEncryptionPublicKey: value.memberEncryptionPublicKey.slice(), joinedAtSequence: entry.sequence }); delete departures[value.account]; };
    const remove = (account: string, departure: GroupDeparture): void => { members.delete(account); departures[account] = departure; };
    switch (payload.kind) {
        case 'update': {
            authorize(owner); const value = payload.value;
            if (value.name !== undefined) state = { ...state, name: value.name };
            if (value.description === null) delete state.description; else if (value.description !== undefined) state = { ...state, description: value.description };
            if (value.memberCapacity !== undefined) state = { ...state, memberCapacity: value.memberCapacity };
            if (value.invitePolicy !== undefined) state = { ...state, invitePolicy: value.invitePolicy };
            break;
        }
        case 'applicationApproval':
            authorize(administrator);
            if (members.size + payload.value.members.length > state.memberCapacity) throw new ProtocolError('group_capacity', 'Group admission exceeds the current capacity.');
            for (const value of payload.value.members) { if (members.has(value.account) || bans.has(value.account)) throw new ProtocolError('invalid_admission', 'An existing or banned account cannot be admitted.'); add(value); }
            advances = true; break;
        case 'roleUpdate': {
            authorize(owner); const target = member(payload.value.account); authorize(target.role !== 'owner'); members.set(target.account, { ...target, role: payload.value.role }); break;
        }
        case 'ownerTransfer': {
            authorize(owner); const target = member(payload.value.newOwnerAccount); authorize(target.account !== actor && !bans.has(target.account));
            members.set(actor, { ...author, role: 'member' }); members.set(target.account, { ...target, role: 'owner' }); state = { ...state, owner: target.account }; break;
        }
        case 'memberLeave': authorize(!owner && payload.value.account === actor); remove(actor, 'left'); advances = true; break;
        case 'memberRemoval':
            authorize(administrator);
            for (const account of payload.value.accounts) { const target = member(account); authorize(account !== actor && target.role !== 'owner' && (owner || target.role === 'member')); remove(account, 'removed'); }
            advances = true; break;
        case 'memberBan':
            authorize(administrator);
            for (const account of payload.value.accounts) {
                const target = members.get(account); authorize(account !== actor && (!target || target.role !== 'owner' && (owner || target.role === 'member')));
                if (target) { remove(account, 'banned'); advances = true; } else departures[account] = 'banned';
                bans.add(account);
            }
            break;
        case 'memberUnban':
            authorize(administrator);
            for (const account of payload.value.accounts) { bans.delete(account); if (departures[account] === 'banned') departures[account] = 'not_member'; }
            break;
        case 'memberRecoveryApproval':
            authorize(administrator);
            for (const value of payload.value.members) {
                const target = member(value.account); authorize(owner || target.role === 'member' && target.account !== actor);
                if (equalBytes(target.memberEncryptionPublicKey, value.memberEncryptionPublicKey)) throw new ProtocolError('unchanged_key', 'Recovered member public key must change.');
                members.set(target.account, { ...target, memberEncryptionPublicKey: value.memberEncryptionPublicKey.slice() });
            }
            advances = true; break;
        case 'secretRotation':
            authorize(owner);
            if (payload.value.clientSecretCommitment === commitment) throw new ProtocolError('unchanged_secret', 'Client secret rotation must change its commitment.');
            commitment = payload.value.clientSecretCommitment;
            if (payload.value.ownerEncryptionPublicKey) {
                if (equalBytes(author.memberEncryptionPublicKey, payload.value.ownerEncryptionPublicKey)) throw new ProtocolError('unchanged_key', 'Specified owner public key must change.');
                members.set(actor, { ...author, memberEncryptionPublicKey: payload.value.ownerEncryptionPublicKey.slice() });
            }
            advances = true; break;
        case 'close': authorize(owner); state = { ...state, status: 'closed' }; break;
    }
    if (advances ? entry.epoch <= previous.epoch : entry.epoch !== previous.epoch) throw new ProtocolError('invalid_epoch', 'Group management event has an invalid epoch transition.');
    state = { ...state, memberCount: members.size };
    return { changeKinds: managementChanges[payload.kind], projection: { ...group, sequence: entry.sequence, epoch: entry.epoch, state, members: [...members.values()], bans: [...bans], departures,
        clientSecretCommitment: commitment, managementHash: groupManagementHash(requireObject(groupManagementPayloadCodec.encode(payload)), context) } };
}
