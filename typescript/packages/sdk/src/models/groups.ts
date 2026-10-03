import { requireLength, verifyDevice } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId, validateRelayId } from '../identity/neo.js';
import { array, bytes, defineCodec, integer, text, type ExtensibleModel, type ValueCodec } from '../protocol/codec.js';
import { signingInput, type NetworkContext } from '../protocol/context.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { canonicalJson, parseJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { containsNonWhitespace, contentReferenceCodec, messageBodyCodec, validateAttachments, validateMessageBody, type ContentReference, type MessageBody } from './content.js';
import { certificateId, validateCertificate, type DeviceCertificate } from './identity.js';
import { encryptedPayloadCodec, validateEncryptedPayload, type EncryptedPayload } from './messages.js';

export interface GroupRef { readonly groupId: string; readonly relayId: string }
export function validateGroupRef(value: GroupRef): void { validateIdentifier('group', value.groupId); validateRelayId(value.relayId); }

export interface GroupSecretBox extends ExtensibleModel { readonly alg: string; readonly enc: Uint8Array; readonly sealedSecret: Uint8Array }
export const groupSecretBoxCodec = defineCodec<GroupSecretBox>({ alg: { wire: 'alg', codec: text }, enc: { wire: 'enc', codec: bytes }, sealedSecret: { wire: 'sealed_secret', codec: bytes } });
export function validateGroupSecretBox(value: GroupSecretBox): void {
    validateCompleteObject(groupSecretBoxCodec.encode(value));
    if (value.alg !== 'X25519-HKDF-SHA256-AES256GCM') throw new ProtocolError('unsupported_algorithm', 'Unsupported group secret box algorithm.');
    requireLength(value.enc, 32, 'Ephemeral public key'); requireLength(value.sealedSecret, 60, 'Sealed group secret');
}

/** The sender identity is carried by the verified timeline event and certificate, never by this envelope. */
export interface GroupMessageEnvelope extends ExtensibleModel {
    readonly messageId: string; readonly groupId: string; readonly epoch: number; readonly createdAt: number;
    readonly payload: EncryptedPayload; readonly deviceSignature: Uint8Array;
}
export const groupMessageEnvelopeCodec = defineCodec<GroupMessageEnvelope>({ messageId: { wire: 'message_id', codec: text }, groupId: { wire: 'group_id', codec: text },
    epoch: { wire: 'epoch', codec: integer }, createdAt: { wire: 'created_at', codec: integer }, payload: { wire: 'payload', codec: encryptedPayloadCodec },
    deviceSignature: { wire: 'device_signature', codec: bytes } }, 'meshline.group.message');
export const groupMessageEnvelopeInput = (value: GroupMessageEnvelope, context: NetworkContext): Uint8Array => signingInput(groupMessageEnvelopeCodec.encode(value), context, ['device_signature']);
export function validateGroupMessageEnvelope(value: GroupMessageEnvelope): void {
    validateCompleteObject(groupMessageEnvelopeCodec.encode(value)); validateIdentifier('message', value.messageId); validateIdentifier('group', value.groupId);
    requireSafeInteger(value.epoch, 0); requireSafeInteger(value.createdAt, 0); requireLength(value.deviceSignature, 64, 'Group message signature'); validateEncryptedPayload(value.payload);
    if (encodeUtf8(groupMessageEnvelopeCodec.stringify(value)).length > 262144) throw new ProtocolError('invalid_size', 'Group message envelope exceeds 262144 canonical UTF-8 bytes.');
}
/** Historical signature integrity only; membership, epoch and event ordering require verified management state. */
export function verifyGroupMessageEnvelope(value: GroupMessageEnvelope, sender: DeviceCertificate, signerDeviceId: string, context: NetworkContext): void {
    validateGroupMessageEnvelope(value); validateCertificate(sender, context); validateIdentifier('device', signerDeviceId);
    if (certificateId(sender, context) !== signerDeviceId) throw new ProtocolError('invalid_identity', 'Group event signing certificate belongs to another device.');
    if (!verifyDevice(groupMessageEnvelopeInput(value, context), value.deviceSignature, sender.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid group message signature.');
}

export interface GroupMessage extends ExtensibleModel { readonly body?: MessageBody; readonly attachments?: readonly ContentReference[]; readonly replyToSeq?: number }
export const groupMessageCodec = defineCodec<GroupMessage>({ body: { wire: 'body', codec: messageBodyCodec, optional: true }, attachments: { wire: 'attachments', codec: array(contentReferenceCodec), optional: true },
    replyToSeq: { wire: 'reply_to_seq', codec: integer, optional: true } }, 'meshline.group.message.content');
export function validateGroupMessage(value: GroupMessage): void {
    validateCompleteObject(groupMessageCodec.encode(value)); if (value.body) validateMessageBody(value.body); if (value.attachments) validateAttachments(value.attachments);
    if (!value.body && !value.attachments?.length) throw new ProtocolError('invalid_message', 'A group message requires a body or at least one attachment.');
    if (value.replyToSeq !== undefined) requireSafeInteger(value.replyToSeq, 1);
}
export interface GroupMemberNicknameUpdate extends ExtensibleModel { readonly nickname: string | null }
export const groupMemberNicknameUpdateCodec = defineCodec<GroupMemberNicknameUpdate>({ nickname: { wire: 'nickname', codec: text, nullable: true } }, 'meshline.group.member.nickname.update');
export function validateGroupMemberNicknameUpdate(value: GroupMemberNicknameUpdate): void {
    const wire = groupMemberNicknameUpdateCodec.encode(value); if (wire.nickname === null) delete wire.nickname; validateCompleteObject(wire);
    if (value.nickname !== null && (!containsNonWhitespace(value.nickname) || encodeUtf8(value.nickname).length > 256)) throw new ProtocolError('invalid_nickname', 'A group nickname requires non-whitespace text and at most 256 UTF-8 bytes.');
}
/** Preserves authenticated but business-invalid content so the receiver can record diagnostics without stalling its cursor. */
export const groupPayloadCodec: ValueCodec<JsonObject> = {
    decode(value) {
        const object = requireObject(value); const copy = requireObject(parseJson(canonicalJson(object)));
        if (typeof copy.$type !== 'string') throw new ProtocolError('invalid_type', 'Group content requires a string $type.');
        const complete = { ...copy }; if (complete.$type === 'meshline.group.member.nickname.update' && complete.nickname === null) delete complete.nickname;
        validateCompleteObject(complete); return copy;
    },
    encode(value) { return this.decode(value); },
};
export function validateGroupPayload(value: JsonObject): void {
    const wire = groupPayloadCodec.decode(value);
    if (wire.$type === 'meshline.group.message.content') validateGroupMessage(groupMessageCodec.decode(wire));
    else if (wire.$type === 'meshline.group.member.nickname.update') validateGroupMemberNicknameUpdate(groupMemberNicknameUpdateCodec.decode(wire));
}

export interface GroupMemberKey extends ExtensibleModel { readonly account: string; readonly memberEncryptionPublicKey: Uint8Array }
export const groupMemberKeyCodec = defineCodec<GroupMemberKey>({ account: { wire: 'account', codec: text }, memberEncryptionPublicKey: { wire: 'member_encryption_public_key', codec: bytes } });
export function validateGroupMemberKey(value: GroupMemberKey): void { validateCompleteObject(groupMemberKeyCodec.encode(value)); validateAccountId(value.account); requireLength(value.memberEncryptionPublicKey, 32, 'Member encryption public key'); }
export interface GroupHistorySecret extends ExtensibleModel { readonly groupId: string; readonly epoch: number; readonly applicationSecret: Uint8Array }
export const groupHistorySecretCodec = defineCodec<GroupHistorySecret>({ groupId: { wire: 'group_id', codec: text }, epoch: { wire: 'epoch', codec: integer }, applicationSecret: { wire: 'application_secret', codec: bytes } });
export function validateGroupHistorySecret(value: GroupHistorySecret): void { validateCompleteObject(groupHistorySecretCodec.encode(value)); validateIdentifier('group', value.groupId); requireSafeInteger(value.epoch, 0); requireLength(value.applicationSecret, 32, 'Epoch application secret'); }
export interface GroupMemberPrivateState extends ExtensibleModel, GroupRef { readonly memberEncryptionPrivateKey: Uint8Array }
export const groupMemberPrivateStateCodec = defineCodec<GroupMemberPrivateState>({ groupId: { wire: 'group_id', codec: text }, relayId: { wire: 'relay_id', codec: text }, memberEncryptionPrivateKey: { wire: 'member_encryption_private_key', codec: bytes } });
export function validateGroupMemberPrivateState(value: GroupMemberPrivateState): void { validateCompleteObject(groupMemberPrivateStateCodec.encode(value)); validateGroupRef(value); requireLength(value.memberEncryptionPrivateKey, 32, 'Member encryption private key'); }
export interface AccountGroupHistorySecretSync extends ExtensibleModel { readonly secrets: readonly GroupHistorySecret[] }
export const accountGroupHistorySecretSyncCodec = defineCodec<AccountGroupHistorySecretSync>({ secrets: { wire: 'secrets', codec: array(groupHistorySecretCodec) } }, 'meshline.account.group.history_secret.sync');
export function validateAccountGroupHistorySecretSync(value: AccountGroupHistorySecretSync): void {
    validateCompleteObject(accountGroupHistorySecretSyncCodec.encode(value)); if (!value.secrets.length) throw new ProtocolError('invalid_size', 'History secret synchronization requires at least one secret.');
    const ids = new Set<string>(); for (const secret of value.secrets) { validateGroupHistorySecret(secret); const id = `${secret.groupId}|${secret.epoch}`;
        if (ids.has(id)) throw new ProtocolError('duplicate_secret', 'History secret synchronization repeats a group and epoch.'); ids.add(id); }
}
export interface AccountGroupPrivateStateSync extends ExtensibleModel { readonly states: readonly GroupMemberPrivateState[] }
export const accountGroupPrivateStateSyncCodec = defineCodec<AccountGroupPrivateStateSync>({ states: { wire: 'states', codec: array(groupMemberPrivateStateCodec) } }, 'meshline.account.group.state.sync');
export function validateAccountGroupPrivateStateSync(value: AccountGroupPrivateStateSync): void {
    validateCompleteObject(accountGroupPrivateStateSyncCodec.encode(value)); if (!value.states.length) throw new ProtocolError('invalid_size', 'Private state synchronization requires at least one state.');
    const ids = new Set<string>(); for (const state of value.states) { validateGroupMemberPrivateState(state); if (ids.has(state.groupId)) throw new ProtocolError('duplicate_group', 'Private state synchronization repeats a group.'); ids.add(state.groupId); }
}
export interface AccountGroupPrivateStateRequest extends ExtensibleModel { readonly groupId?: string }
export const accountGroupPrivateStateRequestCodec = defineCodec<AccountGroupPrivateStateRequest>({ groupId: { wire: 'group_id', codec: text, optional: true } }, 'meshline.account.group.state.request');
export function validateAccountGroupPrivateStateRequest(value: AccountGroupPrivateStateRequest): void { validateCompleteObject(accountGroupPrivateStateRequestCodec.encode(value)); if (value.groupId !== undefined) validateIdentifier('group', value.groupId); }
