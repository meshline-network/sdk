import { ProtocolError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import { array, defineCodec, dictionary, integer, text, type ExtensibleModel } from '../protocol/codec.js';
import type { NetworkContext } from '../protocol/context.js';
import { requireSafeInteger } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { contentHashBytes } from './content.js';
import { groupApplicationApprovalCodec, groupApplicationCodec, groupInviteCodec, groupMemberRecoveryApprovalCodec, groupMemberRecoveryRequestCodec, validateGroupManagementPayload, verifyGroupApplication, verifyGroupInvite, verifyGroupMemberRecoveryRequest,
    type GroupApplication, type GroupApplicationApproval, type GroupInvite, type GroupMemberRecoveryApproval, type GroupMemberRecoveryRequest } from './group-management.js';
import { groupSecretBoxCodec, validateGroupSecretBox, type GroupSecretBox } from './groups.js';
import { certificateId, deviceCertificateCodec, validateCertificate, type DeviceCertificate } from './identity.js';

const groupId = { wire: 'group_id', codec: text } as const;
const signerCertificate = { wire: 'signer_certificate', codec: deviceCertificateCodec } as const;
const acceptedAt = { wire: 'accepted_at', codec: integer } as const;
const next = { wire: 'next', codec: text, optional: true } as const;
const certificates = { wire: 'certificates', codec: array(deviceCertificateCodec) } as const;
function cursor(value: string): void { if (!/^[A-Za-z0-9._~-]+$/.test(value) || /[^A-Za-z0-9._~-]/.test(value)) throw new ProtocolError('invalid_cursor', 'Group list cursor must contain nonempty ASCII URL-safe text.'); }
export interface GroupListQuery extends ExtensibleModel { readonly groupId: string; readonly cursor?: string; readonly limit?: number }
export const groupListQueryCodec = defineCodec<GroupListQuery>({ groupId, cursor: { wire: 'cursor', codec: text, optional: true }, limit: { wire: 'limit', codec: integer, optional: true } });
export function validateGroupListQuery(value: GroupListQuery): void { validateCompleteObject(groupListQueryCodec.encode(value)); validateIdentifier('group', value.groupId); if (value.cursor !== undefined) cursor(value.cursor); if (value.limit !== undefined) requireSafeInteger(value.limit, 1); }
function page(count: number, nextCursor: string | undefined, query: GroupListQuery): void {
    validateGroupListQuery(query); if (nextCursor !== undefined) cursor(nextCursor);
    if (nextCursor !== undefined && (!count || nextCursor === query.cursor) || query.limit !== undefined && count > query.limit) throw new ProtocolError('invalid_pagination', 'Group list is empty with a continuation, repeats its cursor, or exceeds the requested limit.');
}
export interface GroupInviteQuery extends ExtensibleModel { readonly groupId: string; readonly inviteId: string }
export const groupInviteQueryCodec = defineCodec<GroupInviteQuery>({ groupId, inviteId: { wire: 'invite_id', codec: text } });
export function validateGroupInviteQuery(value: GroupInviteQuery): void { validateCompleteObject(groupInviteQueryCodec.encode(value)); validateIdentifier('group', value.groupId); validateIdentifier('invite', value.inviteId); }
export interface GroupResolveQuery extends ExtensibleModel { readonly groupId: string; readonly inviteId?: string }
export const groupResolveQueryCodec = defineCodec<GroupResolveQuery>({ groupId, inviteId: { wire: 'invite_id', codec: text, optional: true } });
export function validateGroupResolveQuery(value: GroupResolveQuery): void { validateCompleteObject(groupResolveQueryCodec.encode(value)); validateIdentifier('group', value.groupId); if (value.inviteId !== undefined) validateIdentifier('invite', value.inviteId); }
export interface GroupAccountsRequest extends ExtensibleModel { readonly groupId: string; readonly accounts: readonly string[] }
export const groupAccountsRequestCodec = defineCodec<GroupAccountsRequest>({ groupId, accounts: { wire: 'accounts', codec: array(text) } });
export function validateGroupAccountsRequest(value: GroupAccountsRequest): void {
    validateCompleteObject(groupAccountsRequestCodec.encode(value)); validateIdentifier('group', value.groupId);
    if (!value.accounts.length || new Set(value.accounts).size !== value.accounts.length) throw new ProtocolError('invalid_accounts', 'Group account list must be nonempty and unique.'); for (const account of value.accounts) validateAccountId(account);
}
export interface GroupInviteResolveResult extends ExtensibleModel { readonly invite: GroupInvite; readonly signerCertificate: DeviceCertificate; readonly uses: number }
export const groupInviteResolveResultCodec = defineCodec<GroupInviteResolveResult>({ invite: { wire: 'invite', codec: groupInviteCodec }, signerCertificate, uses: { wire: 'uses', codec: integer } });
export function validateGroupInviteResolveResult(value: GroupInviteResolveResult, query: GroupInviteQuery, context: NetworkContext, now: number): void {
    validateCompleteObject(groupInviteResolveResultCodec.encode(value)); validateGroupInviteQuery(query); verifyGroupInvite(value.invite, value.signerCertificate, context, now); requireSafeInteger(value.uses, 0);
    if (value.invite.groupId !== query.groupId || value.invite.inviteId !== query.inviteId) throw new ProtocolError('invalid_binding', 'Relay returned another group invitation.');
    if (value.invite.invitee !== undefined && value.uses > 1 || value.invite.maxUses !== undefined && value.uses > value.invite.maxUses) throw new ProtocolError('invalid_uses', 'Invitation use count exceeds its limit.');
}
export interface GroupInviteEntry extends ExtensibleModel { readonly invite: GroupInvite; readonly signerDeviceId: string; readonly uses: number }
export const groupInviteEntryCodec = defineCodec<GroupInviteEntry>({ invite: { wire: 'invite', codec: groupInviteCodec }, signerDeviceId: { wire: 'signer_device_id', codec: text }, uses: { wire: 'uses', codec: integer } });
export interface GroupInvitePage extends ExtensibleModel { readonly invites: readonly GroupInviteEntry[]; readonly certificates: readonly DeviceCertificate[]; readonly next?: string }
export const groupInvitePageCodec = defineCodec<GroupInvitePage>({ invites: { wire: 'invites', codec: array(groupInviteEntryCodec) }, certificates, next });
export function validateGroupInvitePage(value: GroupInvitePage, query: GroupListQuery, context: NetworkContext, now: number): void {
    validateCompleteObject(groupInvitePageCodec.encode(value)); page(value.invites.length, value.next, query);
    const signers = new Map<string, DeviceCertificate>(); for (const certificate of value.certificates) { validateCertificate(certificate, context); const id = certificateId(certificate, context); if (signers.has(id)) throw new ProtocolError('duplicate_certificate', 'Invitation page repeats a signing device.'); signers.set(id, certificate); }
    const ids = new Set<string>(); for (const entry of value.invites) {
        validateIdentifier('device', entry.signerDeviceId); const signer = signers.get(entry.signerDeviceId); if (!signer) throw new ProtocolError('missing_certificate', 'Invitation signing certificate is missing.');
        if (ids.has(entry.invite.inviteId)) throw new ProtocolError('duplicate_invite', 'Invitation page repeats an invitation.'); ids.add(entry.invite.inviteId);
        validateGroupInviteResolveResult({ invite: entry.invite, signerCertificate: signer, uses: entry.uses }, { groupId: query.groupId, inviteId: entry.invite.inviteId }, context, now);
    }
}
export interface GroupApplicationEntry extends ExtensibleModel { readonly application: GroupApplication; readonly signerCertificate: DeviceCertificate; readonly acceptedAt: number }
export const groupApplicationEntryCodec = defineCodec<GroupApplicationEntry>({ application: { wire: 'application', codec: groupApplicationCodec }, signerCertificate, acceptedAt });
export function validateGroupApplicationEntry(value: GroupApplicationEntry, expectedGroupId: string, context: NetworkContext): void {
    validateCompleteObject(groupApplicationEntryCodec.encode(value)); validateIdentifier('group', expectedGroupId); requireSafeInteger(value.acceptedAt, 0); verifyGroupApplication(value.application, value.signerCertificate, context);
    if (value.application.groupId !== expectedGroupId) throw new ProtocolError('invalid_binding', 'Application belongs to another group.');
}
export interface GroupApplicationPage extends ExtensibleModel { readonly applications: readonly GroupApplicationEntry[]; readonly next?: string }
export const groupApplicationPageCodec = defineCodec<GroupApplicationPage>({ applications: { wire: 'applications', codec: array(groupApplicationEntryCodec) }, next });
export function validateGroupApplicationPage(value: GroupApplicationPage, query: GroupListQuery, context: NetworkContext): void {
    validateCompleteObject(groupApplicationPageCodec.encode(value)); page(value.applications.length, value.next, query); const accounts = new Set<string>();
    for (const entry of value.applications) { validateGroupApplicationEntry(entry, query.groupId, context); if (accounts.has(entry.application.account)) throw new ProtocolError('duplicate_account', 'Application page repeats an account.'); accounts.add(entry.application.account); }
}
export interface GroupRecoverySubmitResult extends ExtensibleModel { readonly acceptedAt: number; readonly expiresAt: number }
const recoveryInterval = { acceptedAt, expiresAt: { wire: 'expires_at', codec: integer } } as const;
export const groupRecoverySubmitResultCodec = defineCodec<GroupRecoverySubmitResult>(recoveryInterval);
export function validateGroupRecoverySubmitResult(value: GroupRecoverySubmitResult): void {
    validateCompleteObject(groupRecoverySubmitResultCodec.encode(value)); requireSafeInteger(value.acceptedAt, 0); requireSafeInteger(value.expiresAt, 0);
    if (value.expiresAt <= value.acceptedAt) throw new ProtocolError('invalid_expiry', 'Recovery acceptance interval must have a later expiry.');
}
export interface GroupRecoveryEntry extends ExtensibleModel, GroupRecoverySubmitResult { readonly request: GroupMemberRecoveryRequest; readonly signerCertificate: DeviceCertificate }
export const groupRecoveryEntryCodec = defineCodec<GroupRecoveryEntry>({ request: { wire: 'request', codec: groupMemberRecoveryRequestCodec }, signerCertificate, ...recoveryInterval });
export function validateGroupRecoveryEntry(value: GroupRecoveryEntry, expectedGroupId: string, context: NetworkContext): void {
    validateCompleteObject(groupRecoveryEntryCodec.encode(value)); validateIdentifier('group', expectedGroupId); validateGroupRecoverySubmitResult({ acceptedAt: value.acceptedAt, expiresAt: value.expiresAt }); verifyGroupMemberRecoveryRequest(value.request, value.signerCertificate, context);
    if (value.request.groupId !== expectedGroupId) throw new ProtocolError('invalid_binding', 'Recovery request belongs to another group.');
}
export interface GroupRecoveryPage extends ExtensibleModel { readonly requests: readonly GroupRecoveryEntry[]; readonly next?: string }
export const groupRecoveryPageCodec = defineCodec<GroupRecoveryPage>({ requests: { wire: 'requests', codec: array(groupRecoveryEntryCodec) }, next });
export function validateGroupRecoveryPage(value: GroupRecoveryPage, query: GroupListQuery, context: NetworkContext): void {
    validateCompleteObject(groupRecoveryPageCodec.encode(value)); page(value.requests.length, value.next, query); const accounts = new Set<string>();
    for (const entry of value.requests) { validateGroupRecoveryEntry(entry, query.groupId, context); if (accounts.has(entry.request.account)) throw new ProtocolError('duplicate_account', 'Recovery page repeats an account.'); accounts.add(entry.request.account); }
}
export interface GroupApplicationApproveRequest extends ExtensibleModel { readonly approval: GroupApplicationApproval; readonly clientSecretCommitment: string; readonly clientSecretBoxes: Readonly<Record<string, GroupSecretBox>> }
export interface GroupRecoveryApproveRequest extends ExtensibleModel { readonly approval: GroupMemberRecoveryApproval; readonly clientSecretCommitment: string; readonly clientSecretBoxes: Readonly<Record<string, GroupSecretBox>> }
const wrapping = { clientSecretCommitment: { wire: 'client_secret_commitment', codec: text }, clientSecretBoxes: { wire: 'client_secret_boxes', codec: dictionary(groupSecretBoxCodec) } } as const;
export const groupApplicationApproveRequestCodec = defineCodec<GroupApplicationApproveRequest>({ approval: { wire: 'approval', codec: groupApplicationApprovalCodec }, ...wrapping });
export const groupRecoveryApproveRequestCodec = defineCodec<GroupRecoveryApproveRequest>({ approval: { wire: 'approval', codec: groupMemberRecoveryApprovalCodec }, ...wrapping });
function boxes(value: GroupApplicationApproveRequest | GroupRecoveryApproveRequest): void {
    contentHashBytes(value.clientSecretCommitment);
    if (Object.keys(value.clientSecretBoxes).length !== value.approval.members.length) throw new ProtocolError('invalid_recipients', 'Client secret boxes must match exactly the approved accounts.');
    for (const member of value.approval.members) { if (!Object.hasOwn(value.clientSecretBoxes, member.account)) throw new ProtocolError('invalid_recipients', 'An approved account is missing its client secret box.'); validateGroupSecretBox(value.clientSecretBoxes[member.account]!); }
}
export function validateGroupApplicationApproveRequest(value: GroupApplicationApproveRequest): void { validateCompleteObject(groupApplicationApproveRequestCodec.encode(value)); validateGroupManagementPayload({ kind: 'applicationApproval', value: value.approval }); boxes(value); }
export function validateGroupRecoveryApproveRequest(value: GroupRecoveryApproveRequest): void { validateCompleteObject(groupRecoveryApproveRequestCodec.encode(value)); validateGroupManagementPayload({ kind: 'memberRecoveryApproval', value: value.approval }); boxes(value); }
export interface GroupSubscriptionRequest extends ExtensibleModel { readonly groupIds: readonly string[] }
export const groupSubscriptionRequestCodec = defineCodec<GroupSubscriptionRequest>({ groupIds: { wire: 'group_ids', codec: array(text) } });
export function validateGroupSubscriptionRequest(value: GroupSubscriptionRequest): void { validateCompleteObject(groupSubscriptionRequestCodec.encode(value)); if (new Set(value.groupIds).size !== value.groupIds.length) throw new ProtocolError('duplicate_group', 'Group subscription repeats a group.'); for (const id of value.groupIds) validateIdentifier('group', id); }
export interface GroupChangedNotification extends ExtensibleModel { readonly groupId: string }
export const groupChangedNotificationCodec = defineCodec<GroupChangedNotification>({ groupId });
export function validateGroupChangedNotification(value: GroupChangedNotification): void { validateCompleteObject(groupChangedNotificationCodec.encode(value)); validateIdentifier('group', value.groupId); }
export interface GroupTimelineChangedNotification extends GroupChangedNotification { readonly head: number }
export const groupTimelineChangedNotificationCodec = defineCodec<GroupTimelineChangedNotification>({ groupId, head: { wire: 'head', codec: integer } });
export function validateGroupTimelineChangedNotification(value: GroupTimelineChangedNotification): void { validateCompleteObject(groupTimelineChangedNotificationCodec.encode(value)); validateIdentifier('group', value.groupId); requireSafeInteger(value.head, 0); }
