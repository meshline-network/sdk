import { requireLength, verifyDevice } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { deriveResourceId, validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId, validateRelayId } from '../identity/neo.js';
import { array, boolean, bytes, defineCodec, enumeration, integer, text, type ExtensibleModel, type ValueCodec } from '../protocol/codec.js';
import { signingInput, type NetworkContext } from '../protocol/context.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { containsNonWhitespace, contentReferenceCodec, messageBodyCodec, validateAttachments, validateMessageBody, type ContentReference, type MessageBody } from './content.js';
import { certificateId, deviceCertificateCodec, validateCertificate, type DeviceCertificate } from './identity.js';

export interface ChannelRef { readonly channelId: string; readonly relayId: string }
export interface ChannelPostRef { readonly channel: ChannelRef; readonly sequence: number }
export function validateChannelRef(value: ChannelRef): void { validateIdentifier('channel', value.channelId); validateRelayId(value.relayId); }
export function validateChannelPostRef(value: ChannelPostRef): void { validateChannelRef(value.channel); requireSafeInteger(value.sequence, 1); }
export interface ChannelDescriptor extends ExtensibleModel {
    readonly channelId: string; readonly nonce: Uint8Array; readonly creator: string; readonly relayId: string;
    readonly name: string; readonly description?: string; readonly moderators?: readonly string[];
    readonly revision: number; readonly status: 'active' | 'closed'; readonly createdAt: number; readonly updatedAt: number; readonly deviceSignature: Uint8Array;
}
export const channelDescriptorCodec = defineCodec<ChannelDescriptor>({
    channelId: { wire: 'channel_id', codec: text }, nonce: { wire: 'nonce', codec: bytes }, creator: { wire: 'creator', codec: text }, relayId: { wire: 'relay_id', codec: text },
    name: { wire: 'name', codec: text }, description: { wire: 'description', codec: text, optional: true }, moderators: { wire: 'moderators', codec: array(text), optional: true },
    revision: { wire: 'revision', codec: integer }, status: { wire: 'status', codec: enumeration('active', 'closed') },
    createdAt: { wire: 'created_at', codec: integer }, updatedAt: { wire: 'updated_at', codec: integer }, deviceSignature: { wire: 'device_signature', codec: bytes },
}, 'meshline.channel.descriptor');
export const channelDescriptorInput = (value: ChannelDescriptor, context: NetworkContext): Uint8Array => signingInput(channelDescriptorCodec.encode(value), context, ['device_signature']);
function size(value: JsonObject, limit: number): void { if (encodeUtf8(canonicalJson(value)).length > limit) throw new ProtocolError('invalid_size', `Channel document exceeds ${limit} canonical JSON bytes.`); }
export function validateChannelDescriptor(value: ChannelDescriptor, context: NetworkContext): void {
    const wire = channelDescriptorCodec.encode(value); validateCompleteObject(wire); validateChannelRef(value); validateAccountId(value.creator); requireLength(value.nonce, 16, 'Channel nonce');
    if (!containsNonWhitespace(value.name) || encodeUtf8(value.name).length > 256) throw new ProtocolError('invalid_name', 'Channel name requires text and at most 256 UTF-8 bytes.');
    if (value.description !== undefined && value.description.length > 0 && (!containsNonWhitespace(value.description) || encodeUtf8(value.description).length > 4096)) throw new ProtocolError('invalid_description', 'Nonempty channel description requires text and at most 4096 UTF-8 bytes.');
    requireSafeInteger(value.revision, 0); requireSafeInteger(value.createdAt, 0); requireSafeInteger(value.updatedAt, 0); requireLength(value.deviceSignature, 64, 'Channel signature');
    if (value.revision === 0 && (value.status !== 'active' || value.createdAt !== value.updatedAt)) throw new ProtocolError('invalid_initial_state', 'Initial channel descriptor must be active with equal creation and update times.');
    if (value.moderators) {
        if (value.moderators.length > 10) throw new ProtocolError('invalid_size', 'At most ten channel moderators are permitted.');
        const seen = new Set([value.creator]); for (const account of value.moderators) { validateAccountId(account); if (seen.has(account)) throw new ProtocolError('duplicate_moderator', 'Moderators must be distinct and cannot include the creator.'); seen.add(account); }
    }
    if (deriveResourceId('channel', value.creator, value.relayId, value.nonce, context) !== value.channelId) throw new ProtocolError('invalid_identity', 'Channel ID does not bind its creator, relay, nonce and context.'); size(wire, 8192);
}
export interface ChannelPost extends ExtensibleModel { readonly channelId: string; readonly messageId: string; readonly body?: MessageBody; readonly attachments?: readonly ContentReference[]; readonly deviceSignature: Uint8Array }
export const channelPostCodec = defineCodec<ChannelPost>({ channelId: { wire: 'channel_id', codec: text }, messageId: { wire: 'message_id', codec: text }, body: { wire: 'body', codec: messageBodyCodec, optional: true },
    attachments: { wire: 'attachments', codec: array(contentReferenceCodec), optional: true }, deviceSignature: { wire: 'device_signature', codec: bytes } }, 'meshline.channel.post');
export const channelPostInput = (value: ChannelPost, context: NetworkContext): Uint8Array => signingInput(channelPostCodec.encode(value), context, ['device_signature']);
export function validateChannelPost(value: ChannelPost): void {
    const wire = channelPostCodec.encode(value); validateCompleteObject(wire); validateIdentifier('channel', value.channelId); validateIdentifier('message', value.messageId); requireLength(value.deviceSignature, 64, 'Post signature');
    if (!value.body && !value.attachments?.length) throw new ProtocolError('content_required', 'Channel posts require a body or at least one attachment.');
    if (value.body) validateMessageBody(value.body); if (value.attachments) validateAttachments(value.attachments); size(wire, 65536);
}
export interface ChannelPostEdit extends ExtensibleModel { readonly channelId: string; readonly targetSequence: number; readonly body?: MessageBody | null; readonly attachments?: readonly ContentReference[] | null; readonly deviceSignature: Uint8Array }
export const channelPostEditCodec = defineCodec<ChannelPostEdit>({ channelId: { wire: 'channel_id', codec: text }, targetSequence: { wire: 'target_sequence', codec: integer },
    body: { wire: 'body', codec: messageBodyCodec, optional: true, nullable: true }, attachments: { wire: 'attachments', codec: array(contentReferenceCodec), optional: true, nullable: true },
    deviceSignature: { wire: 'device_signature', codec: bytes } }, 'meshline.channel.post.edit');
export const channelPostEditInput = (value: ChannelPostEdit, context: NetworkContext): Uint8Array => signingInput(channelPostEditCodec.encode(value), context, ['device_signature']);
export function validateChannelPostEdit(value: ChannelPostEdit): void {
    const wire = channelPostEditCodec.encode(value); validateIdentifier('channel', value.channelId); requireSafeInteger(value.targetSequence, 1); requireLength(value.deviceSignature, 64, 'Post edit signature');
    if (value.body === undefined && value.attachments === undefined && !Object.keys(value.additionalProperties ?? {}).length) throw new ProtocolError('empty_edit', 'A channel edit must change at least one field.');
    for (const key of Object.keys(value.additionalProperties ?? {})) if (['$type', '$context', 'channel_id', 'message_id', 'body', 'attachments', 'device_signature'].includes(key)) throw new ProtocolError('invalid_extension', 'Edit extensions cannot replace defined post fields or context.');
    // Only root field deletion markers may be null. Nested values remain complete objects.
    for (const [key, field] of Object.entries(wire)) if (field !== null) validateCompleteObject({ [key]: field });
    if (value.body) validateMessageBody(value.body); if (value.attachments) validateAttachments(value.attachments);
    if (value.body === null && (value.attachments === null || value.attachments?.length === 0)) throw new ProtocolError('content_required', 'An edit cannot remove both body and every attachment.'); size(wire, 65536);
}
/** Applies replacement/deletion/omission in acceptance order and validates the resulting complete post. Does not alter signed evidence. */
export function applyChannelPostEdit(post: ChannelPost, edit: ChannelPostEdit): ChannelPost {
    validateChannelPost(post); validateChannelPostEdit(edit);
    if (post.channelId !== edit.channelId) throw new ProtocolError('invalid_binding', 'Post edit belongs to another channel.');
    const wire = channelPostCodec.encode(post); const changes = channelPostEditCodec.encode(edit);
    for (const [key, value] of Object.entries(changes)) if (!['$type', 'channel_id', 'target_sequence', 'device_signature'].includes(key)) { if (value === null) delete wire[key]; else wire[key] = value; }
    const result = channelPostCodec.decode(wire); validateChannelPost(result); return result;
}
export interface ChannelPostDelete extends ExtensibleModel { readonly channelId: string; readonly targetSequence: number; readonly reason?: string; readonly deviceSignature: Uint8Array }
export const channelPostDeleteCodec = defineCodec<ChannelPostDelete>({ channelId: { wire: 'channel_id', codec: text }, targetSequence: { wire: 'target_sequence', codec: integer }, reason: { wire: 'reason', codec: text, optional: true }, deviceSignature: { wire: 'device_signature', codec: bytes } }, 'meshline.channel.post.delete');
export const channelPostDeleteInput = (value: ChannelPostDelete, context: NetworkContext): Uint8Array => signingInput(channelPostDeleteCodec.encode(value), context, ['device_signature']);
export function validateChannelPostDelete(value: ChannelPostDelete): void {
    const wire = channelPostDeleteCodec.encode(value); validateCompleteObject(wire); validateIdentifier('channel', value.channelId); requireSafeInteger(value.targetSequence, 1); requireLength(value.deviceSignature, 64, 'Post deletion signature');
    if (value.reason !== undefined && !containsNonWhitespace(value.reason)) throw new ProtocolError('invalid_reason', 'Deletion reason must contain text when present.'); size(wire, 65536);
}
export type ChannelPayload = { readonly kind: 'descriptor'; readonly value: ChannelDescriptor } | { readonly kind: 'post'; readonly value: ChannelPost } | { readonly kind: 'edit'; readonly value: ChannelPostEdit } | { readonly kind: 'delete'; readonly value: ChannelPostDelete };
export const channelPayloadCodec: ValueCodec<ChannelPayload> = {
    decode(value) { switch (requireObject(value)['$type']) {
        case 'meshline.channel.descriptor': return { kind: 'descriptor', value: channelDescriptorCodec.decode(value) };
        case 'meshline.channel.post': return { kind: 'post', value: channelPostCodec.decode(value) };
        case 'meshline.channel.post.edit': return { kind: 'edit', value: channelPostEditCodec.decode(value) };
        case 'meshline.channel.post.delete': return { kind: 'delete', value: channelPostDeleteCodec.decode(value) };
        default: throw new ProtocolError('unsupported_type', 'Unsupported channel event payload.');
    } },
    encode(payload) { switch (payload.kind) { case 'descriptor': return channelDescriptorCodec.encode(payload.value); case 'post': return channelPostCodec.encode(payload.value); case 'edit': return channelPostEditCodec.encode(payload.value); case 'delete': return channelPostDeleteCodec.encode(payload.value); } },
};
export function validateChannelPayload(payload: ChannelPayload, context: NetworkContext): void {
    switch (payload.kind) { case 'descriptor': return validateChannelDescriptor(payload.value, context); case 'post': return validateChannelPost(payload.value); case 'edit': return validateChannelPostEdit(payload.value); case 'delete': return validateChannelPostDelete(payload.value); }
}
export const channelPayloadInput = (payload: ChannelPayload, context: NetworkContext): Uint8Array => signingInput(requireObject(channelPayloadCodec.encode(payload)), context, ['device_signature']);
export interface ChannelEvent extends ExtensibleModel { readonly sequence: number; readonly descriptorRev: number; readonly payload: ChannelPayload; readonly acceptedAt: number; readonly signerDeviceId: string }
export const channelEventCodec = defineCodec<ChannelEvent>({ sequence: { wire: 'sequence', codec: integer }, descriptorRev: { wire: 'descriptor_rev', codec: integer }, payload: { wire: 'payload', codec: channelPayloadCodec }, acceptedAt: { wire: 'accepted_at', codec: integer }, signerDeviceId: { wire: 'signer_device_id', codec: text } });
export function validateChannelEvent(value: ChannelEvent, context: NetworkContext): void {
    const { payload, ...outer } = channelEventCodec.encode(value); validateCompleteObject(outer); validateChannelPayload(value.payload, context);
    requireSafeInteger(value.sequence, 0); requireSafeInteger(value.descriptorRev, 0); requireSafeInteger(value.acceptedAt, 0); validateIdentifier('device', value.signerDeviceId);
    if (value.sequence === 0 && (value.payload.kind !== 'descriptor' || value.payload.value.revision !== 0)) throw new ProtocolError('invalid_sequence', 'Sequence zero requires the initial channel descriptor.');
    if (value.payload.kind === 'descriptor' && (value.payload.value.revision !== value.descriptorRev || value.sequence > 0 && value.payload.value.revision === 0)) throw new ProtocolError('invalid_revision', 'Descriptor event revision or initial sequence is inconsistent.');
    if ((value.payload.kind === 'edit' || value.payload.kind === 'delete') && value.payload.value.targetSequence >= value.sequence) throw new ProtocolError('invalid_sequence', 'A channel edit or deletion must target an earlier positive post sequence.');
}
export interface ChannelResolveResult extends ExtensibleModel { readonly descriptor: ChannelDescriptor; readonly signerCertificate: DeviceCertificate }
export const channelResolveResultCodec = defineCodec<ChannelResolveResult>({ descriptor: { wire: 'descriptor', codec: channelDescriptorCodec }, signerCertificate: { wire: 'signer_certificate', codec: deviceCertificateCodec } });
export function verifyChannelDescriptor(value: ChannelResolveResult, context: NetworkContext, channel: ChannelRef): void {
    validateCompleteObject(channelResolveResultCodec.encode(value)); validateChannelRef(channel); validateChannelDescriptor(value.descriptor, context); validateCertificate(value.signerCertificate, context);
    if (value.descriptor.channelId !== channel.channelId || value.descriptor.relayId !== channel.relayId || value.signerCertificate.account !== value.descriptor.creator) throw new ProtocolError('invalid_binding', 'Channel descriptor or signer belongs to another resource or account.');
    if (!verifyDevice(channelDescriptorInput(value.descriptor, context), value.descriptor.deviceSignature, value.signerCertificate.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid channel descriptor signature.');
}
export function verifyChannelEvent(value: ChannelEvent, certificate: DeviceCertificate, descriptor: ChannelDescriptor, context: NetworkContext, channel: ChannelRef): void {
    validateChannelEvent(value, context); validateCertificate(certificate, context); validateChannelRef(channel);
    if (certificateId(certificate, context) !== value.signerDeviceId || value.payload.value.channelId !== channel.channelId) throw new ProtocolError('invalid_binding', 'Channel event signer or resource binding is invalid.');
    if (value.payload.kind === 'descriptor') { verifyChannelDescriptor({ descriptor: value.payload.value, signerCertificate: certificate }, context, channel); return; }
    validateChannelDescriptor(descriptor, context);
    if (descriptor.channelId !== channel.channelId || descriptor.relayId !== channel.relayId || descriptor.revision !== value.descriptorRev || descriptor.status !== 'active'
        || descriptor.creator !== certificate.account && !descriptor.moderators?.includes(certificate.account)) throw new ProtocolError('unauthorized_event', 'Channel content signer is not authorized by the accepted descriptor revision.');
    if (!verifyDevice(channelPayloadInput(value.payload, context), value.payload.value.deviceSignature, certificate.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid channel content signature.');
}
export interface ChannelReadQuery extends ExtensibleModel { readonly channelId: string; readonly before?: number; readonly after?: number; readonly limit?: number }
export const channelReadQueryCodec = defineCodec<ChannelReadQuery>({ channelId: { wire: 'channel_id', codec: text }, before: { wire: 'before', codec: integer, optional: true }, after: { wire: 'after', codec: integer, optional: true }, limit: { wire: 'limit', codec: integer, optional: true } });
export function validateChannelReadQuery(value: ChannelReadQuery): void {
    validateCompleteObject(channelReadQueryCodec.encode(value)); validateIdentifier('channel', value.channelId); if (value.before !== undefined) requireSafeInteger(value.before, 0); if (value.after !== undefined) requireSafeInteger(value.after, -1); if (value.limit !== undefined) requireSafeInteger(value.limit, 1);
    if (value.before !== undefined && value.after !== undefined) throw new ProtocolError('invalid_bounds', 'Channel before and after cursors cannot be combined.');
}
export interface ChannelReadPage extends ExtensibleModel { readonly events: readonly ChannelEvent[]; readonly certificates: readonly DeviceCertificate[]; readonly hasMore: boolean }
export const channelReadPageCodec = defineCodec<ChannelReadPage>({ events: { wire: 'events', codec: array(channelEventCodec) }, certificates: { wire: 'certificates', codec: array(deviceCertificateCodec) }, hasMore: { wire: 'has_more', codec: boolean } });
/** Validates page boundaries and historical certificate coverage; descriptor authorization is checked by the reader before persistence. */
export function validateChannelReadPage(value: ChannelReadPage, query: ChannelReadQuery, context: NetworkContext): ReadonlyMap<string, DeviceCertificate> {
    validateChannelReadQuery(query); const { events, ...outer } = channelReadPageCodec.encode(value); validateCompleteObject(outer);
    if (value.hasMore && !value.events.length || query.limit !== undefined && value.events.length > query.limit) throw new ProtocolError('invalid_page', 'Invalid channel page size or pagination.');
    const certificates = new Map<string, DeviceCertificate>();
    for (const certificate of value.certificates) { validateCertificate(certificate, context); const id = certificateId(certificate, context); if (certificates.has(id)) throw new ProtocolError('duplicate_certificate', 'Channel page contains duplicate device identities.'); certificates.set(id, certificate); }
    let previous = query.after ?? -1; const revisions = new Set<number>();
    for (const event of value.events) {
        validateChannelEvent(event, context);
        if (event.sequence <= previous || query.before !== undefined && event.sequence >= query.before) throw new ProtocolError('invalid_sequence', 'Channel page violates requested bounds or sequence order.'); previous = event.sequence;
        if (!certificates.has(event.signerDeviceId)) throw new ProtocolError('missing_certificate', 'Channel event lacks signing evidence.');
        if (event.payload.kind === 'descriptor') { if (revisions.has(event.descriptorRev)) throw new ProtocolError('duplicate_revision', 'Descriptor revision occurs twice in one page.'); revisions.add(event.descriptorRev); }
    }
    return certificates;
}
export interface ChannelResolveQuery extends ExtensibleModel { readonly channelId: string; readonly revision?: number }
export const channelResolveQueryCodec = defineCodec<ChannelResolveQuery>({ channelId: { wire: 'channel_id', codec: text }, revision: { wire: 'revision', codec: integer, optional: true } });
export interface ChannelCloseRequest extends ExtensibleModel { readonly channelId: string; readonly revision: number; readonly updatedAt: number; readonly deviceSignature: Uint8Array }
export const channelCloseRequestCodec = defineCodec<ChannelCloseRequest>({ channelId: { wire: 'channel_id', codec: text }, revision: { wire: 'revision', codec: integer }, updatedAt: { wire: 'updated_at', codec: integer }, deviceSignature: { wire: 'device_signature', codec: bytes } });
export interface ChannelReportRequest extends ExtensibleModel { readonly channelId: string; readonly targetSequence: number; readonly reason: string }
export const channelReportRequestCodec = defineCodec<ChannelReportRequest>({ channelId: { wire: 'channel_id', codec: text }, targetSequence: { wire: 'target_sequence', codec: integer }, reason: { wire: 'reason', codec: text } });
export function validateChannelReportRequest(value: ChannelReportRequest): void { validateCompleteObject(channelReportRequestCodec.encode(value)); validateIdentifier('channel', value.channelId); requireSafeInteger(value.targetSequence, 1); if (!containsNonWhitespace(value.reason)) throw new ProtocolError('invalid_reason', 'A channel report requires a reason.'); }
export interface ChannelSubscriptionRequest extends ExtensibleModel { readonly channelIds: readonly string[] }
export const channelSubscriptionRequestCodec = defineCodec<ChannelSubscriptionRequest>({ channelIds: { wire: 'channel_ids', codec: array(text) } });
export function validateChannelSubscriptionRequest(value: ChannelSubscriptionRequest): void { validateCompleteObject(channelSubscriptionRequestCodec.encode(value)); const seen = new Set<string>(); for (const id of value.channelIds) { validateIdentifier('channel', id); if (seen.has(id)) throw new ProtocolError('duplicate_channel', 'Subscription contains a duplicate channel.'); seen.add(id); } }
export interface ChannelTimelineChangedNotification extends ExtensibleModel { readonly channelId: string; readonly head: number }
export const channelTimelineChangedCodec = defineCodec<ChannelTimelineChangedNotification>({ channelId: { wire: 'channel_id', codec: text }, head: { wire: 'head', codec: integer } });
export function validateChannelTimelineChanged(value: ChannelTimelineChangedNotification): void { validateCompleteObject(channelTimelineChangedCodec.encode(value)); validateIdentifier('channel', value.channelId); requireSafeInteger(value.head, 0); }
