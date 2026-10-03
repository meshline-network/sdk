import { requireLength, verifyDevice } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import { array, boolean, bytes, defineCodec, enumeration, integer, text, type ExtensibleModel, type ValueCodec } from '../protocol/codec.js';
import { signingInput, type NetworkContext } from '../protocol/context.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { canonicalJson, parseJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { relayFailureCodec, type RelayFailure } from '../transport/relay-error.js';
import { contentReferenceCodec, messageBodyCodec, validateAttachments, validateMessageBody, type ContentReference, type MessageBody } from './content.js';
import { contactAuthorizationCodec, validateContactGrant, validateContactInvite, type ContactAuthorization } from './contacts.js';
import { certificateId, deviceCertificateCodec, validateCertificate, type DeviceCertificate } from './identity.js';

/** Complete wire business object. Unknown types may be stored but cannot authorize protocol state changes. */
export const messagePayloadCodec: ValueCodec<JsonObject> = {
    decode(value) {
        const object = requireObject(value); validateCompleteObject(object);
        if (typeof object['$type'] !== 'string') throw new ProtocolError('invalid_type', 'A message payload requires a string $type.');
        return requireObject(parseJson(canonicalJson(object)));
    },
    encode(value) { return this.decode(value); },
};

export interface EncryptedPayload extends ExtensibleModel { readonly alg: string; readonly nonce: Uint8Array; readonly ciphertext: Uint8Array }
export const encryptedPayloadCodec = defineCodec<EncryptedPayload>({ alg: { wire: 'alg', codec: text }, nonce: { wire: 'nonce', codec: bytes }, ciphertext: { wire: 'ciphertext', codec: bytes } });
export function validateEncryptedPayload(value: EncryptedPayload): void {
    validateCompleteObject(encryptedPayloadCodec.encode(value));
    if (value.alg !== 'AES-256-GCM') throw new ProtocolError('unsupported_algorithm', 'Message payload encryption must use AES-256-GCM.');
    requireLength(value.nonce, 12, 'Payload nonce');
    if (value.ciphertext.length <= 16) throw new ProtocolError('invalid_ciphertext', 'Encrypted payload requires nonempty ciphertext and a GCM tag.');
}
export interface MessageEnvelope extends ExtensibleModel {
    readonly messageId: string; readonly createdAt: number; readonly from: string; readonly fromDeviceId: string; readonly to: string;
    readonly payload: EncryptedPayload; readonly deviceSignature: Uint8Array;
}
export const messageEnvelopeCodec = defineCodec<MessageEnvelope>({ messageId: { wire: 'message_id', codec: text }, createdAt: { wire: 'created_at', codec: integer },
    from: { wire: 'from', codec: text }, fromDeviceId: { wire: 'from_device_id', codec: text }, to: { wire: 'to', codec: text },
    payload: { wire: 'payload', codec: encryptedPayloadCodec }, deviceSignature: { wire: 'device_signature', codec: bytes } }, 'meshline.message.envelope');
export const messageEnvelopeInput = (value: MessageEnvelope, context: NetworkContext): Uint8Array => signingInput(messageEnvelopeCodec.encode(value), context, ['device_signature']);
export function validateMessageEnvelope(value: MessageEnvelope): void {
    validateCompleteObject(messageEnvelopeCodec.encode(value));
    validateIdentifier('message', value.messageId); validateIdentifier('device', value.fromDeviceId); requireSafeInteger(value.createdAt, 0);
    validateAccountId(value.from); validateAccountId(value.to); requireLength(value.deviceSignature, 64, 'Envelope signature'); validateEncryptedPayload(value.payload);
    if (encodeUtf8(messageEnvelopeCodec.stringify(value)).length > 262144) throw new ProtocolError('invalid_size', 'Message envelope exceeds 262144 canonical UTF-8 bytes.');
}
/** Historical signature integrity only. Current device authority is checked separately by the receiver's business workflow. */
export function verifyMessageEnvelope(value: MessageEnvelope, sender: DeviceCertificate, context: NetworkContext): void {
    validateMessageEnvelope(value); validateCertificate(sender, context);
    if (sender.account !== value.from || certificateId(sender, context) !== value.fromDeviceId) throw new ProtocolError('invalid_identity', 'Envelope signing certificate belongs to another account or device.');
    if (!verifyDevice(messageEnvelopeInput(value, context), value.deviceSignature, sender.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid message envelope signature.');
}
export interface MessageKeyBox extends ExtensibleModel { readonly deviceId: string; readonly alg: string; readonly enc: Uint8Array; readonly sealedKey: Uint8Array }
export const messageKeyBoxCodec = defineCodec<MessageKeyBox>({ deviceId: { wire: 'device_id', codec: text }, alg: { wire: 'alg', codec: text }, enc: { wire: 'enc', codec: bytes }, sealedKey: { wire: 'sealed_key', codec: bytes } });
export function validateMessageKeyBox(value: MessageKeyBox): void {
    validateCompleteObject(messageKeyBoxCodec.encode(value)); validateIdentifier('device', value.deviceId);
    if (value.alg !== 'X25519-HKDF-SHA256-AES256GCM') throw new ProtocolError('unsupported_algorithm', 'Unsupported message key box algorithm.');
    requireLength(value.enc, 32, 'Ephemeral public key'); requireLength(value.sealedKey, 60, 'Sealed content key');
}
export function validateMessageKeyBoxes(values: readonly MessageKeyBox[]): void {
    if (values.length < 1 || values.length > 8) throw new ProtocolError('invalid_size', 'Key box sets require between one and eight devices.');
    const ids = new Set<string>();
    for (const value of values) { validateMessageKeyBox(value); if (ids.has(value.deviceId)) throw new ProtocolError('duplicate_device', 'Key box set contains a duplicate device.'); ids.add(value.deviceId); }
}
export interface MessageSendRequest extends ExtensibleModel {
    readonly envelope: MessageEnvelope; readonly senderBoxes?: readonly MessageKeyBox[]; readonly recipientBoxes: readonly MessageKeyBox[]; readonly authorization?: ContactAuthorization;
}
export const messageSendRequestCodec = defineCodec<MessageSendRequest>({ envelope: { wire: 'envelope', codec: messageEnvelopeCodec }, senderBoxes: { wire: 'sender_boxes', codec: array(messageKeyBoxCodec), optional: true },
    recipientBoxes: { wire: 'recipient_boxes', codec: array(messageKeyBoxCodec) }, authorization: { wire: 'authorization', codec: contactAuthorizationCodec, optional: true } });
export function validateMessageSendRequest(value: MessageSendRequest, now: number): void {
    validateCompleteObject(messageSendRequestCodec.encode(value)); requireSafeInteger(now, 0);
    validateMessageEnvelope(value.envelope); validateMessageKeyBoxes(value.recipientBoxes);
    if (value.senderBoxes) validateMessageKeyBoxes(value.senderBoxes);
    if (value.envelope.from === value.envelope.to && (value.senderBoxes !== undefined || value.authorization !== undefined))
        throw new ProtocolError('invalid_self_delivery', 'Self messages must omit sender boxes and contact authorization.');
    if (value.authorization) {
        if ('grantor' in value.authorization) {
            validateContactGrant(value.authorization, now);
            if (value.authorization.grantor !== value.envelope.to || value.authorization.grantee !== value.envelope.from) throw new ProtocolError('invalid_authorization', 'Message grant has inconsistent account bindings.');
        } else {
            validateContactInvite(value.authorization, now);
            if (value.authorization.inviter !== value.envelope.to) throw new ProtocolError('invalid_authorization', 'Message invitation belongs to another recipient.');
        }
    }
}
export interface DirectMessageReference extends ExtensibleModel { readonly from: string; readonly messageId: string }
export const directMessageReferenceCodec = defineCodec<DirectMessageReference>({ from: { wire: 'from', codec: text }, messageId: { wire: 'message_id', codec: text } });
export interface DirectMessage extends ExtensibleModel { readonly body?: MessageBody; readonly attachments?: readonly ContentReference[]; readonly replyTo?: DirectMessageReference }
export const directMessageCodec = defineCodec<DirectMessage>({ body: { wire: 'body', codec: messageBodyCodec, optional: true }, attachments: { wire: 'attachments', codec: array(contentReferenceCodec), optional: true },
    replyTo: { wire: 'reply_to', codec: directMessageReferenceCodec, optional: true } }, 'meshline.message.direct');
export function validateDirectMessage(value: DirectMessage): void {
    validateCompleteObject(directMessageCodec.encode(value));
    if (value.body) validateMessageBody(value.body);
    if (value.attachments) validateAttachments(value.attachments);
    if (!value.body && !value.attachments?.length) throw new ProtocolError('invalid_message', 'A direct message requires a body or at least one attachment.');
    if (value.replyTo) { validateAccountId(value.replyTo.from); validateIdentifier('message', value.replyTo.messageId); }
}
export interface MessageTimelineEntry extends ExtensibleModel { readonly sequence: number; readonly envelope: MessageEnvelope; readonly keyBox: MessageKeyBox; readonly acceptedAt: number }
export const messageTimelineEntryCodec = defineCodec<MessageTimelineEntry>({ sequence: { wire: 'sequence', codec: integer }, envelope: { wire: 'envelope', codec: messageEnvelopeCodec }, keyBox: { wire: 'key_box', codec: messageKeyBoxCodec }, acceptedAt: { wire: 'accepted_at', codec: integer } });
export interface MessageTimelinePage extends ExtensibleModel { readonly items: readonly MessageTimelineEntry[]; readonly certificates: readonly DeviceCertificate[]; readonly hasMore: boolean; readonly hasRetentionGap?: boolean }
export const messageTimelinePageCodec = defineCodec<MessageTimelinePage>({ items: { wire: 'items', codec: array(messageTimelineEntryCodec) }, certificates: { wire: 'certificates', codec: array(deviceCertificateCodec) },
    hasMore: { wire: 'has_more', codec: boolean }, hasRetentionGap: { wire: 'has_retention_gap', codec: boolean, optional: true } });
/** Checks page ordering and certificate availability. Each entry still requires decryption and business validation before cursor commit. */
export function validateMessageTimelinePage(value: MessageTimelinePage, context: NetworkContext, after = -1): void {
    requireSafeInteger(after, -1); validateCompleteObject(messageTimelinePageCodec.encode(value));
    if (value.hasMore && !value.items.length) throw new ProtocolError('invalid_pagination', 'An empty timeline page cannot have more items.');
    const ids = new Map<string, string>();
    for (const certificate of value.certificates) { validateCertificate(certificate, context); const id = certificateId(certificate, context);
        if (ids.has(id)) throw new ProtocolError('duplicate_device', 'Timeline page has duplicate signing certificates.'); ids.set(id, certificate.account); }
    for (const item of value.items) {
        requireSafeInteger(item.sequence, 0); requireSafeInteger(item.acceptedAt, 0);
        if (item.sequence <= after) throw new ProtocolError('invalid_sequence', 'Timeline sequences must increase strictly after the requested cursor.');
        if (!ids.has(item.envelope.fromDeviceId)) throw new ProtocolError('missing_certificate', 'Timeline message has no signing certificate.');
        if (ids.get(item.envelope.fromDeviceId) !== item.envelope.from) throw new ProtocolError('invalid_identity', 'Timeline signing certificate belongs to another account.');
        after = item.sequence;
    }
}
export interface MessageTimelineQuery extends ExtensibleModel { readonly after?: number; readonly limit?: number }
export const messageTimelineQueryCodec = defineCodec<MessageTimelineQuery>({ after: { wire: 'after', codec: integer, optional: true }, limit: { wire: 'limit', codec: integer, optional: true } });
export function validateMessageTimelineQuery(value: MessageTimelineQuery): void {
    validateCompleteObject(messageTimelineQueryCodec.encode(value));
    if (value.after !== undefined) requireSafeInteger(value.after, -1);
    if (value.limit !== undefined) requireSafeInteger(value.limit, 1);
}
export interface MessageDeliveryQuery extends ExtensibleModel { readonly messageId: string }
export const messageDeliveryQueryCodec = defineCodec<MessageDeliveryQuery>({ messageId: { wire: 'message_id', codec: text } });
export type MessageDeliveryState = 'delivering' | 'target_accepted' | 'failed';
export interface MessageDeliveryStatus extends ExtensibleModel { readonly status: MessageDeliveryState; readonly error?: RelayFailure; readonly acceptedAt: number }
export const messageDeliveryStatusCodec = defineCodec<MessageDeliveryStatus>({ status: { wire: 'status', codec: enumeration('delivering', 'target_accepted', 'failed') }, error: { wire: 'error', codec: relayFailureCodec, optional: true }, acceptedAt: { wire: 'accepted_at', codec: integer } });
export function validateMessageDeliveryStatus(value: MessageDeliveryStatus): void {
    validateCompleteObject(messageDeliveryStatusCodec.encode(value)); requireSafeInteger(value.acceptedAt, 0);
    if ((value.status === 'failed') !== (value.error !== undefined)) throw new ProtocolError('invalid_delivery', 'Only failed delivery results must contain an error.');
}
export interface DeviceStateChanged extends ExtensibleModel { readonly revision: number }
export const deviceStateChangedCodec = defineCodec<DeviceStateChanged>({ revision: { wire: 'revision', codec: integer } }, 'meshline.device.state.changed');
export function validateDeviceStateChanged(value: DeviceStateChanged): void { validateCompleteObject(deviceStateChangedCodec.encode(value)); requireSafeInteger(value.revision, 0); }
export interface MessageTimelineChangedNotification extends ExtensibleModel { readonly head: number }
export const messageTimelineChangedCodec = defineCodec<MessageTimelineChangedNotification>({ head: { wire: 'head', codec: integer } });
export function validateMessageTimelineChanged(value: MessageTimelineChangedNotification): void { validateCompleteObject(messageTimelineChangedCodec.encode(value)); requireSafeInteger(value.head, 0); }
