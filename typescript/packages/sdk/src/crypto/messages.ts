import { ProtocolError } from '../errors.js';
import type { DeviceSigner } from '../interactions.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import { contactAuthorizationCodec, type ContactAuthorization } from '../models/contacts.js';
import { certificateId, deviceCertificateCodec, validateCertificate, type DeviceCertificate } from '../models/identity.js';
import { messageEnvelopeCodec, messageEnvelopeInput, messageKeyBoxCodec, messagePayloadCodec, validateMessageKeyBox, validateMessageSendRequest, verifyMessageEnvelope,
    type MessageEnvelope, type MessageKeyBox, type MessageSendRequest } from '../models/messages.js';
import { signingInput, type NetworkContext } from '../protocol/context.js';
import { concatBytes, encodeBase64Url, encodeUtf8 } from '../protocol/encoding.js';
import { canonicalBytes, parseJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { throwIfAborted } from '../runtime/clock.js';
import { agreeKey, decryptAes, deriveKey, encryptAes, encryptionPublicKey, requireLength, sha256, systemRandom, type RandomSource } from './primitives.js';

const keyBoxSalt = sha256(encodeUtf8('Meshline/keybox-salt/v1'));
type MessageHeader = Pick<MessageEnvelope, 'messageId' | 'createdAt' | 'from' | 'fromDeviceId' | 'to'>;
function validateHeader(value: MessageHeader): void {
    validateIdentifier('message', value.messageId); validateIdentifier('device', value.fromDeviceId); validateAccountId(value.from); validateAccountId(value.to); requireSafeInteger(value.createdAt, 0);
}
/** Fixed protocol projection; envelope extensions are signed but are not part of payload AAD. */
export function messageAad(value: MessageHeader, context: NetworkContext): Uint8Array {
    validateHeader(value);
    return signingInput({ $type: 'meshline.message.aad', message_id: value.messageId, created_at: value.createdAt, from: value.from, from_device_id: value.fromDeviceId, to: value.to }, context);
}
/** Fixed projection shared by HKDF info and AES-GCM key wrapping AAD. */
export function messageKeyBoxAad(value: MessageHeader & { readonly payload: { readonly alg: string } }, box: Pick<MessageKeyBox, 'alg' | 'deviceId' | 'enc'>, account: string, context: NetworkContext): Uint8Array {
    validateHeader(value); validateAccountId(account); validateIdentifier('device', box.deviceId); requireLength(box.enc, 32, 'Ephemeral public key');
    if (account !== value.from && account !== value.to) throw new ProtocolError('invalid_identity', 'Key box account must be a message participant.');
    if (box.alg !== 'X25519-HKDF-SHA256-AES256GCM' || value.payload.alg !== 'AES-256-GCM') throw new ProtocolError('unsupported_algorithm', 'Unsupported message key wrapping algorithms.');
    return signingInput({ $type: 'meshline.message.key_box.aad', alg: box.alg, payload_alg: value.payload.alg, message_id: value.messageId,
        created_at: value.createdAt, from: value.from, from_device_id: value.fromDeviceId, account, device_id: box.deviceId, enc: encodeBase64Url(box.enc) }, context);
}
export interface EncryptMessageOptions {
    readonly context: NetworkContext; readonly signer: DeviceSigner; readonly messageId: string; readonly createdAt: number;
    readonly recipient: string; readonly payload: JsonObject; readonly recipientDevices: readonly DeviceCertificate[];
    readonly senderDevices?: readonly DeviceCertificate[]; readonly authorization?: ContactAuthorization; readonly random?: RandomSource; readonly signal?: AbortSignal;
}
/** Builds immutable delivery material. Device lists must come from current authoritative states, checked by the calling manager. */
export async function encryptMessage(options: EncryptMessageOptions): Promise<MessageSendRequest> {
    const { context, signer, messageId, createdAt, recipient, signal } = options; throwIfAborted(signal);
    const random = options.random ?? systemRandom;
    const certificate = deviceCertificateCodec.decode(deviceCertificateCodec.encode(signer.certificate)); validateCertificate(certificate, context);
    const senderId = certificateId(certificate, context);
    const recipients = copyDevices(options.recipientDevices, recipient, createdAt, context);
    const senders = recipient === certificate.account ? undefined : copyDevices(options.senderDevices ?? [certificate], certificate.account, createdAt, context);
    const authorization = options.authorization === undefined ? undefined : contactAuthorizationCodec.decode(contactAuthorizationCodec.encode(options.authorization));
    if (createdAt < certificate.notBefore || createdAt >= certificate.expiresAt) throw new ProtocolError('unauthorized_device', 'Message signer certificate is outside its validity window.');
    if (recipient === certificate.account && (authorization !== undefined || options.senderDevices !== undefined)) throw new ProtocolError('invalid_self_delivery', 'Self messages must omit sender devices and authorization.');
    const header = { messageId, createdAt, from: certificate.account, fromDeviceId: senderId, to: recipient };
    const aad = messageAad(header, context);
    const plaintext = canonicalBytes(messagePayloadCodec.decode(options.payload));
    let contentKey: Uint8Array | undefined;
    try {
        contentKey = random.bytes(32); requireLength(contentKey, 32, 'Content key');
        const nonce = random.bytes(12); requireLength(nonce, 12, 'Payload nonce');
        let envelope: MessageEnvelope = { ...header, payload: { alg: 'AES-256-GCM', nonce, ciphertext: encryptAes(contentKey, nonce, plaintext, aad) }, deviceSignature: new Uint8Array(64) };
        envelope = { ...envelope, deviceSignature: (await signer.sign(messageEnvelopeInput(envelope, context), signal)).slice() };
        throwIfAborted(signal);
        if (certificateId(signer.certificate, context) !== senderId) throw new ProtocolError('invalid_identity', 'Device signer changed identity during message signing.');
        verifyMessageEnvelope(envelope, certificate, context);
        const recipientBoxes = sealKeys(envelope, recipient, recipients, contentKey, context, random);
        const senderBoxes = senders && sealKeys(envelope, certificate.account, senders, contentKey, context, random);
        const result: MessageSendRequest = { envelope, recipientBoxes, ...(senderBoxes ? { senderBoxes } : {}), ...(authorization ? { authorization } : {}) };
        validateMessageSendRequest(result, createdAt); throwIfAborted(signal); return result;
    } finally { contentKey?.fill(0); plaintext.fill(0); }
}
function copyDevices(values: readonly DeviceCertificate[], account: string, now: number, context: NetworkContext): DeviceCertificate[] {
    if (values.length < 1 || values.length > 8) throw new ProtocolError('invalid_size', 'Message encryption requires between one and eight devices per account.');
    const ids = new Set<string>();
    return values.map(value => {
        const copy = deviceCertificateCodec.decode(deviceCertificateCodec.encode(value)); validateCertificate(copy, context);
        if (copy.account !== account || copy.notBefore > now || now >= copy.expiresAt) throw new ProtocolError('unauthorized_device', 'Encryption certificate belongs to another account or is outside its validity window.');
        const id = certificateId(copy, context); if (ids.has(id)) throw new ProtocolError('duplicate_device', 'Duplicate encryption device.'); ids.add(id); return copy;
    });
}
function sealKeys(envelope: MessageEnvelope, account: string, certificates: readonly DeviceCertificate[], contentKey: Uint8Array, context: NetworkContext, random: RandomSource): MessageKeyBox[] {
    return certificates.map(certificate => {
        let ephemeral: Uint8Array | undefined; let shared: Uint8Array | undefined; let wrappingKey: Uint8Array | undefined;
        try {
            ephemeral = random.bytes(32); requireLength(ephemeral, 32, 'Ephemeral private key');
            const box = { deviceId: certificateId(certificate, context), alg: 'X25519-HKDF-SHA256-AES256GCM', enc: encryptionPublicKey(ephemeral) };
            shared = agreeKey(ephemeral, certificate.encryptionPublicKey);
            const aad = messageKeyBoxAad(envelope, box, account, context); wrappingKey = deriveKey(shared, keyBoxSalt, aad);
            const nonce = random.bytes(12); requireLength(nonce, 12, 'Key box nonce');
            return { ...box, sealedKey: concatBytes(nonce, encryptAes(wrappingKey, nonce, contentKey, aad)) };
        } finally { ephemeral?.fill(0); shared?.fill(0); wrappingKey?.fill(0); }
    });
}
/** Implementations return a fresh 32-byte shared secret; this function owns and erases that buffer. */
export interface MessageDecryptor { readonly certificate: DeviceCertificate; deriveSharedSecret(peerPublicKey: Uint8Array, signal?: AbortSignal): Promise<Uint8Array> }
/** Local key access failures are retryable reception failures, never grounds for discarding a remote message. */
export class MessageKeyAccessError extends Error { override readonly name = 'MessageKeyAccessError'; }
export interface DecryptMessageOptions {
    readonly context: NetworkContext; readonly receiver: MessageDecryptor; readonly envelope: MessageEnvelope; readonly keyBox: MessageKeyBox;
    readonly sender: DeviceCertificate; readonly signal?: AbortSignal;
}
/** Verifies signature before key agreement and returns no plaintext until both GCM authentications and complete JSON validation pass. */
export async function decryptMessage(options: DecryptMessageOptions): Promise<JsonObject> {
    const { context, receiver, signal } = options; throwIfAborted(signal);
    const envelope = messageEnvelopeCodec.decode(messageEnvelopeCodec.encode(options.envelope));
    const box = messageKeyBoxCodec.decode(messageKeyBoxCodec.encode(options.keyBox));
    const sender = deviceCertificateCodec.decode(deviceCertificateCodec.encode(options.sender));
    verifyMessageEnvelope(envelope, sender, context); validateMessageKeyBox(box);
    // Public low-order-point validation distinguishes malformed remote input from failures opening local protected keys.
    const probe = agreeKey(new Uint8Array(32).fill(1), box.enc); probe.fill(0);
    let local: DeviceCertificate;
    try { local = deviceCertificateCodec.decode(deviceCertificateCodec.encode(receiver.certificate)); validateCertificate(local, context); }
    catch (cause) { throw new MessageKeyAccessError('Local device certificate is unavailable or invalid.', { cause }); }
    const localId = certificateId(local, context);
    if (local.account !== envelope.from && local.account !== envelope.to || box.deviceId !== localId) throw new ProtocolError('invalid_identity', 'Message or key box belongs to another account or device.');
    let shared: Uint8Array | undefined; let wrappingKey: Uint8Array | undefined; let contentKey: Uint8Array | undefined; let plaintext: Uint8Array | undefined;
    try {
        try {
            shared = await receiver.deriveSharedSecret(box.enc.slice(), signal); throwIfAborted(signal);
            if (certificateId(receiver.certificate, context) !== localId) throw new Error('Decrypting device changed identity during key agreement.');
            requireLength(shared, 32, 'X25519 shared secret');
            if (shared.every(byte => byte === 0)) throw new MessageKeyAccessError('Device key agreement produced an all-zero shared secret.');
        } catch (cause) {
            throwIfAborted(signal);
            if (cause instanceof MessageKeyAccessError) throw cause;
            throw new MessageKeyAccessError('Local device key agreement failed.', { cause });
        }
        const aad = messageKeyBoxAad(envelope, box, local.account, context); wrappingKey = deriveKey(shared, keyBoxSalt, aad);
        contentKey = decryptAes(wrappingKey, box.sealedKey.subarray(0, 12), box.sealedKey.subarray(12), aad);
        requireLength(contentKey, 32, 'Content key');
        plaintext = decryptAes(contentKey, envelope.payload.nonce, envelope.payload.ciphertext, messageAad(envelope, context));
        return messagePayloadCodec.decode(parseJson(plaintext));
    } finally { shared?.fill(0); wrappingKey?.fill(0); contentKey?.fill(0); plaintext?.fill(0); }
}
