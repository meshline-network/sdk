import { requireLength, verifyDevice } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import { array, boolean, bytes, defineCodec, enumeration, integer, text, type ExtensibleModel, type ProtocolCodec, type ValueCodec } from '../protocol/codec.js';
import { signingInput, type NetworkContext } from '../protocol/context.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { canonicalJson, parseJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { containsNonWhitespace, contentHashBytes } from './content.js';
import { certificateId, deviceCertificateCodec, validateCertificate, type DeviceCertificate } from './identity.js';
import { groupMemberKeyCodec, groupSecretBoxCodec, validateGroupMemberKey, validateGroupSecretBox, type GroupMemberKey, type GroupSecretBox } from './groups.js';

export type GroupStatus = 'active' | 'closed';
export type GroupRole = 'owner' | 'administrator' | 'member';
export type GroupInvitePolicy = 'administrators' | 'members_targeted' | 'members_shareable';
const policy = enumeration('administrators', 'members_targeted', 'members_shareable');
const role = enumeration('owner', 'administrator', 'member');
function name(value: string): void { if (!containsNonWhitespace(value) || encodeUtf8(value).length > 256) throw new ProtocolError('invalid_name', 'Group name requires text and at most 256 UTF-8 bytes.'); }
function description(value: string | undefined): void { if (value !== undefined && value !== '' && (!containsNonWhitespace(value) || encodeUtf8(value).length > 4096)) throw new ProtocolError('invalid_description', 'Nonempty group description requires text and at most 4096 UTF-8 bytes.'); }
const signed = { groupId: { wire: 'group_id', codec: text }, deviceSignature: { wire: 'device_signature', codec: bytes } } as const;
const management = { ...signed, prevHash: { wire: 'prev_hash', codec: text } } as const;
export interface GroupCreate extends ExtensibleModel {
    readonly groupId: string; readonly nonce: Uint8Array; readonly name: string; readonly description?: string; readonly memberCapacity: number;
    readonly invitePolicy: GroupInvitePolicy; readonly owner: GroupMemberKey; readonly clientSecretCommitment: string; readonly deviceSignature: Uint8Array;
}
export const groupCreateCodec = defineCodec<GroupCreate>({ ...signed, nonce: { wire: 'nonce', codec: bytes }, name: { wire: 'name', codec: text }, description: { wire: 'description', codec: text, optional: true },
    memberCapacity: { wire: 'member_capacity', codec: integer }, invitePolicy: { wire: 'invite_policy', codec: policy }, owner: { wire: 'owner', codec: groupMemberKeyCodec }, clientSecretCommitment: { wire: 'client_secret_commitment', codec: text } }, 'meshline.group.create');
export const groupCreateInput = (value: GroupCreate, context: NetworkContext): Uint8Array => signingInput(groupCreateCodec.encode(value), context, ['device_signature']);
export function validateGroupCreate(value: GroupCreate): void {
    validateCompleteObject(groupCreateCodec.encode(value)); validateIdentifier('group', value.groupId); requireLength(value.nonce, 16, 'Group nonce'); name(value.name); description(value.description);
    requireSafeInteger(value.memberCapacity, 1); validateGroupMemberKey(value.owner); contentHashBytes(value.clientSecretCommitment); requireLength(value.deviceSignature, 64, 'Group signature');
}
export interface GroupCreateRequest extends ExtensibleModel { readonly create: GroupCreate; readonly clientSecretBox: GroupSecretBox }
export const groupCreateRequestCodec = defineCodec<GroupCreateRequest>({ create: { wire: 'create', codec: groupCreateCodec }, clientSecretBox: { wire: 'client_secret_box', codec: groupSecretBoxCodec } });
export function validateGroupCreateRequest(value: GroupCreateRequest): void { validateCompleteObject(groupCreateRequestCodec.encode(value)); validateGroupCreate(value.create); validateGroupSecretBox(value.clientSecretBox); }
export interface GroupManagementOperation extends ExtensibleModel { readonly groupId: string; readonly prevHash: string; readonly deviceSignature: Uint8Array }
function validateOperation(value: GroupManagementOperation): void { validateIdentifier('group', value.groupId); contentHashBytes(value.prevHash); requireLength(value.deviceSignature, 64, 'Group management signature'); }
export interface GroupUpdate extends GroupManagementOperation { readonly name?: string; readonly description?: string | null; readonly memberCapacity?: number; readonly invitePolicy?: GroupInvitePolicy }
export const groupUpdateCodec = defineCodec<GroupUpdate>({ ...management, name: { wire: 'name', codec: text, optional: true }, description: { wire: 'description', codec: text, optional: true, nullable: true },
    memberCapacity: { wire: 'member_capacity', codec: integer, optional: true }, invitePolicy: { wire: 'invite_policy', codec: policy, optional: true } }, 'meshline.group.update');
export function validateGroupUpdate(value: GroupUpdate): void {
    const wire = groupUpdateCodec.encode(value); if (wire.description === null) delete wire.description; validateCompleteObject(wire); validateOperation(value);
    if (value.name === undefined && value.description === undefined && value.memberCapacity === undefined && value.invitePolicy === undefined) throw new ProtocolError('empty_update', 'A group update requires a known modifiable field.');
    if (value.name !== undefined) name(value.name); if (value.description !== null) description(value.description); if (value.memberCapacity !== undefined) requireSafeInteger(value.memberCapacity, 1);
}
export interface GroupApplicationApproval extends GroupManagementOperation { readonly members: readonly GroupMemberKey[] }
export const groupApplicationApprovalCodec = defineCodec<GroupApplicationApproval>({ ...management, members: { wire: 'members', codec: array(groupMemberKeyCodec) } }, 'meshline.group.application.approval');
export interface GroupMemberRecoveryApproval extends GroupManagementOperation { readonly members: readonly GroupMemberKey[] }
export const groupMemberRecoveryApprovalCodec = defineCodec<GroupMemberRecoveryApproval>({ ...management, members: { wire: 'members', codec: array(groupMemberKeyCodec) } }, 'meshline.group.member.recovery.approval');
function validateMembers(members: readonly GroupMemberKey[]): void {
    if (!members.length) throw new ProtocolError('invalid_size', 'A member list cannot be empty.'); const seen = new Set<string>();
    for (const member of members) { validateGroupMemberKey(member); if (seen.has(member.account)) throw new ProtocolError('duplicate_member', 'A member list repeats an account.'); seen.add(member.account); }
}
export interface GroupRoleUpdate extends GroupManagementOperation { readonly account: string; readonly role: 'administrator' | 'member' }
export const groupRoleUpdateCodec = defineCodec<GroupRoleUpdate>({ ...management, account: { wire: 'account', codec: text }, role: { wire: 'role', codec: enumeration('administrator', 'member') } }, 'meshline.group.role.update');
export interface GroupOwnerTransfer extends GroupManagementOperation { readonly newOwnerAccount: string }
export const groupOwnerTransferCodec = defineCodec<GroupOwnerTransfer>({ ...management, newOwnerAccount: { wire: 'new_owner_account', codec: text } }, 'meshline.group.owner.transfer');
export interface GroupMemberLeave extends GroupManagementOperation { readonly account: string }
export const groupMemberLeaveCodec = defineCodec<GroupMemberLeave>({ ...management, account: { wire: 'account', codec: text } }, 'meshline.group.member.leave');
export interface GroupMemberRemoval extends GroupManagementOperation { readonly accounts: readonly string[] }
export const groupMemberRemovalCodec = defineCodec<GroupMemberRemoval>({ ...management, accounts: { wire: 'accounts', codec: array(text) } }, 'meshline.group.member.removal');
export interface GroupMemberBan extends GroupManagementOperation { readonly accounts: readonly string[] }
export const groupMemberBanCodec = defineCodec<GroupMemberBan>({ ...management, accounts: { wire: 'accounts', codec: array(text) } }, 'meshline.group.member.ban');
export interface GroupMemberUnban extends GroupManagementOperation { readonly accounts: readonly string[] }
export const groupMemberUnbanCodec = defineCodec<GroupMemberUnban>({ ...management, accounts: { wire: 'accounts', codec: array(text) } }, 'meshline.group.member.unban');
function validateAccounts(accounts: readonly string[]): void {
    if (!accounts.length) throw new ProtocolError('invalid_size', 'An account list cannot be empty.'); const seen = new Set<string>();
    for (const account of accounts) { validateAccountId(account); if (seen.has(account)) throw new ProtocolError('duplicate_account', 'An account list repeats an account.'); seen.add(account); }
}
export interface GroupSecretRotation extends GroupManagementOperation { readonly clientSecretCommitment: string; readonly ownerEncryptionPublicKey?: Uint8Array }
export const groupSecretRotationCodec = defineCodec<GroupSecretRotation>({ ...management, clientSecretCommitment: { wire: 'client_secret_commitment', codec: text }, ownerEncryptionPublicKey: { wire: 'owner_encryption_public_key', codec: bytes, optional: true } }, 'meshline.group.secret.rotation');
export interface GroupClose extends GroupManagementOperation {}
export const groupCloseCodec = defineCodec<GroupClose>(management, 'meshline.group.close');
export interface GroupKeyRotated extends ExtensibleModel {}
export const groupKeyRotatedCodec = defineCodec<GroupKeyRotated>({}, 'meshline.group.key.rotated');

const managementCodecs = { create: groupCreateCodec, update: groupUpdateCodec, applicationApproval: groupApplicationApprovalCodec, roleUpdate: groupRoleUpdateCodec,
    ownerTransfer: groupOwnerTransferCodec, memberLeave: groupMemberLeaveCodec, memberRemoval: groupMemberRemovalCodec, memberBan: groupMemberBanCodec,
    memberUnban: groupMemberUnbanCodec, memberRecoveryApproval: groupMemberRecoveryApprovalCodec, secretRotation: groupSecretRotationCodec, close: groupCloseCodec } as const;
type ManagementModels = { [K in keyof typeof managementCodecs]: ReturnType<(typeof managementCodecs)[K]['decode']> };
export type GroupManagementPayload = { [K in keyof ManagementModels]: { readonly kind: K; readonly value: ManagementModels[K] } }[keyof ManagementModels];
const managementKinds: Readonly<Record<string, keyof ManagementModels>> = { 'meshline.group.create': 'create', 'meshline.group.update': 'update', 'meshline.group.application.approval': 'applicationApproval',
    'meshline.group.role.update': 'roleUpdate', 'meshline.group.owner.transfer': 'ownerTransfer', 'meshline.group.member.leave': 'memberLeave', 'meshline.group.member.removal': 'memberRemoval',
    'meshline.group.member.ban': 'memberBan', 'meshline.group.member.unban': 'memberUnban', 'meshline.group.member.recovery.approval': 'memberRecoveryApproval', 'meshline.group.secret.rotation': 'secretRotation', 'meshline.group.close': 'close' };
export const groupManagementPayloadCodec: ValueCodec<GroupManagementPayload> = {
    decode(value) {
        const wire = requireObject(value); const kind = typeof wire.$type === 'string' && Object.hasOwn(managementKinds, wire.$type) ? managementKinds[wire.$type] : undefined;
        if (!kind) throw new ProtocolError('unsupported_group_operation', 'Unknown group administration type; state verification must pause.');
        return { kind, value: managementCodecs[kind].decode(wire) } as GroupManagementPayload;
    },
    encode(value) {
        const codec = managementCodecs[value.kind] as ProtocolCodec<GroupManagementPayload['value']> | undefined;
        if (!codec) throw new ProtocolError('unsupported_group_operation', 'Unknown group administration kind.'); return codec.encode(value.value);
    },
};
export const groupManagementInput = (value: GroupManagementPayload, context: NetworkContext): Uint8Array => signingInput(requireObject(groupManagementPayloadCodec.encode(value)), context, ['device_signature']);
export function validateGroupManagementPayload(value: GroupManagementPayload): void {
    if (value.kind === 'create') { validateGroupCreate(value.value); return; }
    if (value.kind === 'update') { validateGroupUpdate(value.value); return; }
    validateCompleteObject(requireObject(groupManagementPayloadCodec.encode(value))); validateOperation(value.value);
    switch (value.kind) {
        case 'applicationApproval': case 'memberRecoveryApproval': validateMembers(value.value.members); break;
        case 'memberRemoval': case 'memberBan': case 'memberUnban': validateAccounts(value.value.accounts); break;
        case 'roleUpdate': case 'memberLeave': validateAccountId(value.value.account); break;
        case 'ownerTransfer': validateAccountId(value.value.newOwnerAccount); break;
        case 'secretRotation': contentHashBytes(value.value.clientSecretCommitment); if (value.value.ownerEncryptionPublicKey) requireLength(value.value.ownerEncryptionPublicKey, 32, 'Owner encryption public key'); break;
        case 'close': break;
    }
}
export function verifyGroupManagementPayload(value: GroupManagementPayload, certificate: DeviceCertificate, context: NetworkContext): void {
    validateGroupManagementPayload(value); validateCertificate(certificate, context);
    if (!verifyDevice(groupManagementInput(value, context), value.value.deviceSignature, certificate.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid group administration signature.');
}

export interface GroupInvite extends ExtensibleModel { readonly inviteId: string; readonly groupId: string; readonly inviter: string; readonly invitee?: string; readonly maxUses?: number; readonly createdAt: number; readonly expiresAt: number; readonly deviceSignature: Uint8Array }
export const groupInviteCodec = defineCodec<GroupInvite>({ ...signed, inviteId: { wire: 'invite_id', codec: text }, inviter: { wire: 'inviter', codec: text }, invitee: { wire: 'invitee', codec: text, optional: true }, maxUses: { wire: 'max_uses', codec: integer, optional: true },
    createdAt: { wire: 'created_at', codec: integer }, expiresAt: { wire: 'expires_at', codec: integer } }, 'meshline.group.invite');
export const groupInviteInput = (value: GroupInvite, context: NetworkContext): Uint8Array => signingInput(groupInviteCodec.encode(value), context, ['device_signature']);
export function validateGroupInvite(value: GroupInvite, now: number): void {
    validateCompleteObject(groupInviteCodec.encode(value)); validateIdentifier('invite', value.inviteId); validateIdentifier('group', value.groupId); validateAccountId(value.inviter);
    if (value.invitee !== undefined) validateAccountId(value.invitee); if (value.maxUses !== undefined) requireSafeInteger(value.maxUses, 1);
    if (value.invitee !== undefined && value.maxUses !== undefined) throw new ProtocolError('invalid_invite', 'Only shareable invitations may specify a use limit.');
    requireSafeInteger(value.createdAt, 0); requireSafeInteger(value.expiresAt, 0); requireSafeInteger(now, 0); requireLength(value.deviceSignature, 64, 'Group invitation signature');
    if (value.expiresAt <= value.createdAt || value.expiresAt <= now) throw new ProtocolError('expired_invite', 'Group invitation must expire after its creation and the current time.');
}
export function verifyGroupInvite(value: GroupInvite, certificate: DeviceCertificate, context: NetworkContext, now: number): void {
    validateGroupInvite(value, now); validateCertificate(certificate, context);
    if (certificate.account !== value.inviter) throw new ProtocolError('invalid_identity', 'Group invitation certificate belongs to another account.');
    if (!verifyDevice(groupInviteInput(value, context), value.deviceSignature, certificate.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid group invitation signature.');
}
export interface GroupApplication extends ExtensibleModel { readonly groupId: string; readonly inviteId: string; readonly account: string; readonly memberEncryptionPublicKey: Uint8Array; readonly deviceSignature: Uint8Array }
const memberRequest = { ...signed, account: { wire: 'account', codec: text }, memberEncryptionPublicKey: { wire: 'member_encryption_public_key', codec: bytes } } as const;
export const groupApplicationCodec = defineCodec<GroupApplication>({ ...memberRequest, inviteId: { wire: 'invite_id', codec: text } }, 'meshline.group.application');
export const groupApplicationInput = (value: GroupApplication, context: NetworkContext): Uint8Array => signingInput(groupApplicationCodec.encode(value), context, ['device_signature']);
export function validateGroupApplication(value: GroupApplication): void { validateCompleteObject(groupApplicationCodec.encode(value)); validateIdentifier('group', value.groupId); validateIdentifier('invite', value.inviteId); validateAccountId(value.account); requireLength(value.memberEncryptionPublicKey, 32, 'Member encryption public key'); requireLength(value.deviceSignature, 64, 'Application signature'); }
export interface GroupMemberRecoveryRequest extends ExtensibleModel { readonly groupId: string; readonly account: string; readonly memberEncryptionPublicKey: Uint8Array; readonly deviceSignature: Uint8Array }
export const groupMemberRecoveryRequestCodec = defineCodec<GroupMemberRecoveryRequest>(memberRequest, 'meshline.group.member.recovery');
export const groupMemberRecoveryRequestInput = (value: GroupMemberRecoveryRequest, context: NetworkContext): Uint8Array => signingInput(groupMemberRecoveryRequestCodec.encode(value), context, ['device_signature']);
export function validateGroupMemberRecoveryRequest(value: GroupMemberRecoveryRequest): void { validateCompleteObject(groupMemberRecoveryRequestCodec.encode(value)); validateIdentifier('group', value.groupId); validateAccountId(value.account); requireLength(value.memberEncryptionPublicKey, 32, 'Member encryption public key'); requireLength(value.deviceSignature, 64, 'Recovery request signature'); }
export function verifyGroupApplication(value: GroupApplication, certificate: DeviceCertificate, context: NetworkContext): void {
    validateGroupApplication(value); validateCertificate(certificate, context); if (certificate.account !== value.account) throw new ProtocolError('invalid_identity', 'Application certificate belongs to another account.');
    if (!verifyDevice(groupApplicationInput(value, context), value.deviceSignature, certificate.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid group application signature.');
}
export function verifyGroupMemberRecoveryRequest(value: GroupMemberRecoveryRequest, certificate: DeviceCertificate, context: NetworkContext): void {
    validateGroupMemberRecoveryRequest(value); validateCertificate(certificate, context); if (certificate.account !== value.account) throw new ProtocolError('invalid_identity', 'Recovery request certificate belongs to another account.');
    if (!verifyDevice(groupMemberRecoveryRequestInput(value, context), value.deviceSignature, certificate.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid member recovery signature.');
}

export interface GroupState extends ExtensibleModel { readonly groupId: string; readonly name: string; readonly description?: string; readonly status: GroupStatus; readonly owner: string; readonly memberCapacity: number; readonly memberCount: number; readonly invitePolicy: GroupInvitePolicy }
export const groupStateCodec = defineCodec<GroupState>({ groupId: { wire: 'group_id', codec: text }, name: { wire: 'name', codec: text }, description: { wire: 'description', codec: text, optional: true }, status: { wire: 'status', codec: enumeration('active', 'closed') },
    owner: { wire: 'owner', codec: text }, memberCapacity: { wire: 'member_capacity', codec: integer }, memberCount: { wire: 'member_count', codec: integer }, invitePolicy: { wire: 'invite_policy', codec: policy } });
/** A relay preview is structurally validated, but cannot establish membership or replace verified chain state. */
export function validateGroupState(value: GroupState): void { validateCompleteObject(groupStateCodec.encode(value)); validateIdentifier('group', value.groupId); name(value.name); description(value.description); validateAccountId(value.owner); requireSafeInteger(value.memberCapacity, 1); requireSafeInteger(value.memberCount, 1); }
export interface GroupMember extends GroupMemberKey { readonly role: GroupRole }
export const groupMemberCodec = defineCodec<GroupMember>({ account: { wire: 'account', codec: text }, memberEncryptionPublicKey: { wire: 'member_encryption_public_key', codec: bytes }, role: { wire: 'role', codec: role } });
export interface GroupSequenceQuery extends ExtensibleModel { readonly groupId: string; readonly after?: number; readonly limit?: number }
export const groupSequenceQueryCodec = defineCodec<GroupSequenceQuery>({ groupId: { wire: 'group_id', codec: text }, after: { wire: 'after', codec: integer, optional: true }, limit: { wire: 'limit', codec: integer, optional: true } });
export function validateGroupSequenceQuery(value: GroupSequenceQuery): void { validateCompleteObject(groupSequenceQueryCodec.encode(value)); validateIdentifier('group', value.groupId); if (value.after !== undefined) requireSafeInteger(value.after, -1); if (value.limit !== undefined) requireSafeInteger(value.limit, 1); }
const timelinePayload: ValueCodec<JsonObject> = {
    decode(value) { const wire = requireObject(parseJson(canonicalJson(requireObject(value)))); if (typeof wire.$type !== 'string') throw new ProtocolError('invalid_type', 'A group event payload requires a string $type.'); return wire; },
    encode(value) { return this.decode(value); },
};
/** Raw typed payload lets the receiver quarantine invalid ordinary messages without skipping unknown administration events. */
export interface GroupEvent extends ExtensibleModel { readonly sequence: number; readonly epoch: number; readonly payload: JsonObject; readonly acceptedAt: number; readonly signerDeviceId?: string }
export const groupEventCodec = defineCodec<GroupEvent>({ sequence: { wire: 'sequence', codec: integer }, epoch: { wire: 'epoch', codec: integer }, payload: { wire: 'payload', codec: timelinePayload }, acceptedAt: { wire: 'accepted_at', codec: integer }, signerDeviceId: { wire: 'signer_device_id', codec: text, optional: true } });
export function validateGroupEvent(value: GroupEvent): void {
    const wire = groupEventCodec.encode(value); delete wire.payload; validateCompleteObject(wire); requireSafeInteger(value.sequence, 0); requireSafeInteger(value.epoch, 0); requireSafeInteger(value.acceptedAt, 0);
    if (value.payload.$type === 'meshline.group.key.rotated') {
        validateCompleteObject(value.payload); if (value.signerDeviceId !== undefined || Object.hasOwn(value.payload, 'device_signature')) throw new ProtocolError('invalid_rotation', 'Relay rotation cannot carry a device signer or signature.');
    } else { if (value.signerDeviceId === undefined) throw new ProtocolError('missing_signer', 'A client group event requires a signing device.'); validateIdentifier('device', value.signerDeviceId); }
}
export interface GroupSyncPage extends ExtensibleModel { readonly events: readonly GroupEvent[]; readonly certificates: readonly DeviceCertificate[]; readonly hasMore: boolean }
export const groupSyncPageCodec = defineCodec<GroupSyncPage>({ events: { wire: 'events', codec: array(groupEventCodec) }, certificates: { wire: 'certificates', codec: array(deviceCertificateCodec) }, hasMore: { wire: 'has_more', codec: boolean } });
export function validateGroupSyncPage(value: GroupSyncPage, query: GroupSequenceQuery, context: NetworkContext): void {
    validateGroupSequenceQuery(query); const wire = groupSyncPageCodec.encode(value); delete wire.events; validateCompleteObject(wire);
    if (value.hasMore && !value.events.length || query.limit !== undefined && value.events.length > query.limit) throw new ProtocolError('invalid_pagination', 'Invalid group synchronization page length.');
    const ids = new Set<string>(); for (const certificate of value.certificates) { validateCertificate(certificate, context); const id = certificateId(certificate, context); if (ids.has(id)) throw new ProtocolError('duplicate_device', 'Group page repeats a signing certificate.'); ids.add(id); }
    let previous = query.after ?? -1;
    for (const entry of value.events) { validateGroupEvent(entry); if (entry.sequence <= previous) throw new ProtocolError('invalid_sequence', 'Group events must be in increasing order after the requested cursor.'); previous = entry.sequence;
        if (entry.signerDeviceId !== undefined && !ids.has(entry.signerDeviceId)) throw new ProtocolError('missing_certificate', 'Group event signing certificate is missing.'); }
}
