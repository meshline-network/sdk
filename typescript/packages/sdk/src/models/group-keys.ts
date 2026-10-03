import { ProtocolError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import { array, boolean, defineCodec, dictionary, integer, text, type ExtensibleModel } from '../protocol/codec.js';
import { requireSafeInteger } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { contentHashBytes } from './content.js';
import { groupSecretBoxCodec, validateGroupSecretBox, type GroupSecretBox } from './groups.js';

export interface GroupKeyEntry extends ExtensibleModel { readonly epoch: number; readonly clientSecretBox?: GroupSecretBox; readonly relaySecretBox: GroupSecretBox }
export const groupKeyEntryCodec = defineCodec<GroupKeyEntry>({ epoch: { wire: 'epoch', codec: integer }, clientSecretBox: { wire: 'client_secret_box', codec: groupSecretBoxCodec, optional: true }, relaySecretBox: { wire: 'relay_secret_box', codec: groupSecretBoxCodec } });
export function validateGroupKeyEntry(value: GroupKeyEntry): void { validateCompleteObject(groupKeyEntryCodec.encode(value)); requireSafeInteger(value.epoch, 0); if (value.clientSecretBox) validateGroupSecretBox(value.clientSecretBox); validateGroupSecretBox(value.relaySecretBox); }
export interface GroupKeyPage extends ExtensibleModel { readonly keys: readonly GroupKeyEntry[]; readonly hasMore: boolean }
export const groupKeyPageCodec = defineCodec<GroupKeyPage>({ keys: { wire: 'keys', codec: array(groupKeyEntryCodec) }, hasMore: { wire: 'has_more', codec: boolean } });
export function validateGroupKeyPage(value: GroupKeyPage, after = -1): void {
    validateCompleteObject(groupKeyPageCodec.encode(value)); requireSafeInteger(after, -1);
    if (value.hasMore && !value.keys.length) throw new ProtocolError('invalid_pagination', 'An empty group key page cannot have more entries.');
    if (value.keys.length && !value.keys[0]!.clientSecretBox) throw new ProtocolError('missing_secret_box', 'The first group key entry in each page requires a client secret box.');
    for (const entry of value.keys) { validateGroupKeyEntry(entry); if (entry.epoch <= after) throw new ProtocolError('invalid_epoch', 'Group key epochs must increase after the requested cursor.'); after = entry.epoch; }
}
export interface GroupRotationPrepareRequest extends ExtensibleModel { readonly groupId: string; readonly baseCommitment: string; readonly clientSecretCommitment: string; readonly clientSecretBoxes: Readonly<Record<string, GroupSecretBox>> }
export const groupRotationPrepareRequestCodec = defineCodec<GroupRotationPrepareRequest>({ groupId: { wire: 'group_id', codec: text }, baseCommitment: { wire: 'base_commitment', codec: text }, clientSecretCommitment: { wire: 'client_secret_commitment', codec: text }, clientSecretBoxes: { wire: 'client_secret_boxes', codec: dictionary(groupSecretBoxCodec) } });
export function validateGroupRotationPrepareRequest(value: GroupRotationPrepareRequest): void {
    validateCompleteObject(groupRotationPrepareRequestCodec.encode(value)); validateIdentifier('group', value.groupId); contentHashBytes(value.baseCommitment); contentHashBytes(value.clientSecretCommitment);
    if (value.baseCommitment === value.clientSecretCommitment) throw new ProtocolError('unchanged_secret', 'Prepared client secret commitment must change.');
    const boxes = Object.entries(value.clientSecretBoxes); if (!boxes.length) throw new ProtocolError('invalid_size', 'Rotation preparation requires at least one client secret box.');
    for (const [account, box] of boxes) { validateAccountId(account); validateGroupSecretBox(box); }
}
export interface GroupRotationPrepareResult extends ExtensibleModel { readonly prepared: number; readonly expiresAt: number }
export const groupRotationPrepareResultCodec = defineCodec<GroupRotationPrepareResult>({ prepared: { wire: 'prepared', codec: integer }, expiresAt: { wire: 'expires_at', codec: integer } });
export function validateGroupRotationPrepareResult(value: GroupRotationPrepareResult, now: number): void {
    validateCompleteObject(groupRotationPrepareResultCodec.encode(value)); requireSafeInteger(value.prepared, 0); requireSafeInteger(value.expiresAt, 0); requireSafeInteger(now, 0);
    if (value.expiresAt <= now) throw new ProtocolError('expired_preparation', 'Group rotation preparation has expired.');
}
