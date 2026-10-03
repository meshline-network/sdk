import { ProtocolError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import type { DeviceSigner } from '../interactions.js';
import { contentHashBytes } from '../models/content.js';
import { certificateId, deviceCertificateCodec, validateCertificate, type DeviceCertificate } from '../models/identity.js';
import { groupMessageEnvelopeCodec, groupMessageEnvelopeInput, groupPayloadCodec, validateGroupPayload, validateGroupSecretBox, verifyGroupMessageEnvelope,
    type GroupMessageEnvelope, type GroupSecretBox } from '../models/groups.js';
import { signingInput, type NetworkContext } from '../protocol/context.js';
import { concatBytes, encodeBase64Url, encodeUtf8 } from '../protocol/encoding.js';
import { canonicalBytes, parseJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { throwIfAborted } from '../runtime/clock.js';
import { agreeKey, decryptAes, deriveKey, encryptAes, encryptionPublicKey, requireLength, sha256, systemRandom, type RandomSource } from './primitives.js';

const boxSalt = sha256(encodeUtf8('Meshline/keybox-salt/v1'));
const epochSalt = sha256(encodeUtf8('Meshline/group-epoch-salt/v1'));
const messageSalt = sha256(encodeUtf8('Meshline/group-message-salt/v1'));
/** Local protection/key-access failures remain retryable and never reject remote group messages. */
export class GroupKeyAccessError extends Error { override readonly name = 'GroupKeyAccessError'; }
/** Hash the complete signed management object, including its device signature; timeline metadata is external. */
export function groupManagementHash(value: JsonObject, context: NetworkContext): string {
    if (typeof value.$type !== 'string') throw new ProtocolError('invalid_type', 'Management hashes require a typed protocol object.');
    return `sha256:${encodeBase64Url(sha256(signingInput(value, context)))}`;
}
export function groupClientSecretCommitment(groupId: string, secret: Uint8Array, context: NetworkContext): string {
    validateIdentifier('group', groupId); requireLength(secret, 32, 'Client group secret');
    return groupManagementHash({ $type: 'meshline.group.client_secret.commitment', group_id: groupId, secret: encodeBase64Url(secret) }, context);
}
export interface GroupClientSecretBoxHeader { readonly groupId: string; readonly account: string; readonly memberEncryptionPublicKey: Uint8Array; readonly clientSecretCommitment: string }
export function groupClientSecretBoxAad(value: GroupClientSecretBoxHeader, context: NetworkContext): Uint8Array {
    validateIdentifier('group', value.groupId); validateAccountId(value.account); requireLength(value.memberEncryptionPublicKey, 32, 'Member encryption public key'); contentHashBytes(value.clientSecretCommitment);
    return signingInput({ $type: 'meshline.group.client_secret_box.aad', group_id: value.groupId, account: value.account,
        member_encryption_public_key: encodeBase64Url(value.memberEncryptionPublicKey), client_secret_commitment: value.clientSecretCommitment }, context);
}
export interface GroupRelaySecretBoxHeader { readonly groupId: string; readonly account: string; readonly deviceId: string; readonly epoch: number }
export function groupRelaySecretBoxAad(value: GroupRelaySecretBoxHeader, context: NetworkContext): Uint8Array {
    validateIdentifier('group', value.groupId); validateAccountId(value.account); validateIdentifier('device', value.deviceId); requireSafeInteger(value.epoch, 0);
    return signingInput({ $type: 'meshline.group.relay_secret_box.aad', group_id: value.groupId, account: value.account, device_id: value.deviceId, epoch: value.epoch }, context);
}
/** Low-level secret wrapping with a protocol AAD projection; caller-owned secret and public key are never erased. */
export function sealGroupSecret(secret: Uint8Array, publicKey: Uint8Array, aad: Uint8Array, random: RandomSource = systemRandom): GroupSecretBox {
    requireLength(secret, 32, 'Group secret'); requireLength(publicKey, 32, 'Secret recipient public key');
    let ephemeral: Uint8Array | undefined; let shared: Uint8Array | undefined; let key: Uint8Array | undefined;
    try {
        ephemeral = random.bytes(32); requireLength(ephemeral, 32, 'Ephemeral private key'); const enc = encryptionPublicKey(ephemeral);
        shared = agreeKey(ephemeral, publicKey); key = deriveKey(shared, boxSalt, aad);
        const nonce = random.bytes(12); requireLength(nonce, 12, 'Secret box nonce');
        return { alg: 'X25519-HKDF-SHA256-AES256GCM', enc, sealedSecret: concatBytes(nonce, encryptAes(key, nonce, secret, aad)) };
    } finally { ephemeral?.fill(0); shared?.fill(0); key?.fill(0); }
}
/** Caller owns both the supplied shared secret and the returned plaintext; erase them after use. */
export function openGroupSecret(box: GroupSecretBox, shared: Uint8Array, aad: Uint8Array): Uint8Array {
    validateGroupSecretBox(box); requireLength(shared, 32, 'Secret box shared secret');
    if (shared.every(byte => byte === 0)) throw new ProtocolError('invalid_key', 'An all-zero shared secret is forbidden.');
    const key = deriveKey(shared, boxSalt, aad);
    try { return decryptAes(key, box.sealedSecret.subarray(0, 12), box.sealedSecret.subarray(12), aad); }
    finally { key.fill(0); }
}
export function sealGroupClientSecret(header: GroupClientSecretBoxHeader, secret: Uint8Array, context: NetworkContext, random: RandomSource = systemRandom): GroupSecretBox {
    const aad = groupClientSecretBoxAad(header, context);
    if (groupClientSecretCommitment(header.groupId, secret, context) !== header.clientSecretCommitment) throw new ProtocolError('invalid_commitment', 'Client group secret does not match its commitment.');
    return sealGroupSecret(secret, header.memberEncryptionPublicKey, aad, random);
}
/** Validates the client half before combining both independently supplied 32-byte secrets. */
export function deriveGroupApplicationSecret(groupId: string, epoch: number, clientSecretCommitment: string, clientSecret: Uint8Array, relaySecret: Uint8Array, context: NetworkContext): Uint8Array {
    validateIdentifier('group', groupId); requireSafeInteger(epoch, 0); contentHashBytes(clientSecretCommitment);
    requireLength(clientSecret, 32, 'Client group secret'); requireLength(relaySecret, 32, 'Relay epoch secret');
    if (groupClientSecretCommitment(groupId, clientSecret, context) !== clientSecretCommitment) throw new ProtocolError('invalid_commitment', 'Client group secret does not match its commitment.');
    const material = concatBytes(clientSecret, relaySecret);
    try { return deriveKey(material, epochSalt, signingInput({ $type: 'meshline.group.epoch_secret', group_id: groupId, epoch, client_secret_commitment: clientSecretCommitment }, context)); }
    finally { material.fill(0); }
}
export type GroupMessageHeader = Pick<GroupMessageEnvelope, 'groupId' | 'epoch' | 'messageId' | 'createdAt'>;
export function groupMessageAad(value: GroupMessageHeader, account: string, deviceId: string, context: NetworkContext): Uint8Array {
    validateIdentifier('group', value.groupId); validateIdentifier('message', value.messageId); requireSafeInteger(value.epoch, 0); requireSafeInteger(value.createdAt, 0); validateAccountId(account); validateIdentifier('device', deviceId);
    return signingInput({ $type: 'meshline.group.message.aad', group_id: value.groupId, epoch: value.epoch, message_id: value.messageId, created_at: value.createdAt, from: account, from_device_id: deviceId }, context);
}
export function deriveGroupMessageKey(applicationSecret: Uint8Array, aad: Uint8Array): Uint8Array { requireLength(applicationSecret, 32, 'Epoch application secret'); return deriveKey(applicationSecret, messageSalt, aad); }
export interface EncryptGroupMessageOptions extends GroupMessageHeader {
    readonly context: NetworkContext; readonly signer: DeviceSigner; readonly applicationSecret: Uint8Array; readonly payload: JsonObject; readonly random?: RandomSource; readonly signal?: AbortSignal;
}
export async function encryptGroupMessage(options: EncryptGroupMessageOptions): Promise<GroupMessageEnvelope> {
    const { context, signer, signal } = options; throwIfAborted(signal);
    const certificate = deviceCertificateCodec.decode(deviceCertificateCodec.encode(signer.certificate)); validateCertificate(certificate, context);
    const signerId = certificateId(certificate, context); const header = { groupId: options.groupId, epoch: options.epoch, messageId: options.messageId, createdAt: options.createdAt };
    if (header.createdAt < certificate.notBefore || header.createdAt >= certificate.expiresAt) throw new ProtocolError('unauthorized_device', 'Group signer certificate is outside its validity window.');
    const payload = groupPayloadCodec.decode(options.payload); validateGroupPayload(payload);
    const aad = groupMessageAad(header, certificate.account, signerId, context); const key = deriveGroupMessageKey(options.applicationSecret, aad); let plaintext: Uint8Array | undefined;
    try {
        plaintext = canonicalBytes(payload); const nonce = (options.random ?? systemRandom).bytes(12); requireLength(nonce, 12, 'Group message nonce');
        let envelope: GroupMessageEnvelope = { ...header, payload: { alg: 'AES-256-GCM', nonce, ciphertext: encryptAes(key, nonce, plaintext, aad) }, deviceSignature: new Uint8Array(64) };
        envelope = { ...envelope, deviceSignature: (await signer.sign(groupMessageEnvelopeInput(envelope, context), signal)).slice() }; throwIfAborted(signal);
        if (certificateId(signer.certificate, context) !== signerId) throw new ProtocolError('invalid_identity', 'Group signer changed identity during signing.');
        verifyGroupMessageEnvelope(envelope, certificate, signerId, context); return envelope;
    } finally { key.fill(0); plaintext?.fill(0); }
}
export interface DecryptGroupMessageOptions {
    readonly context: NetworkContext; readonly envelope: GroupMessageEnvelope; readonly sender: DeviceCertificate; readonly signerDeviceId: string; readonly applicationSecret: Uint8Array;
}
/** Authenticates the signature, sender account and device before returning typed content. Membership validation remains mandatory in the receiver. */
export function decryptGroupMessage(options: DecryptGroupMessageOptions): JsonObject {
    const { context, signerDeviceId } = options; const envelope = groupMessageEnvelopeCodec.decode(groupMessageEnvelopeCodec.encode(options.envelope));
    const sender = deviceCertificateCodec.decode(deviceCertificateCodec.encode(options.sender)); verifyGroupMessageEnvelope(envelope, sender, signerDeviceId, context);
    const key = deriveGroupMessageKey(options.applicationSecret, groupMessageAad(envelope, sender.account, signerDeviceId, context)); let plaintext: Uint8Array | undefined;
    try { plaintext = decryptAes(key, envelope.payload.nonce, envelope.payload.ciphertext, groupMessageAad(envelope, sender.account, signerDeviceId, context)); return groupPayloadCodec.decode(parseJson(plaintext)); }
    finally { key.fill(0); plaintext?.fill(0); }
}
