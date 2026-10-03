import { expect, test, vi } from 'vitest';
import {
    NetworkContext, agreeKey, canonicalBytes, canonicalJson, certificateId, concatBytes, decodeBase64Url, decryptAes, decryptMessage, deriveKey, deviceCertificateCodec,
    directMessageCodec, encryptAes, encryptMessage, encodeUtf8, encryptionPublicKey, messageAad, messageEnvelopeCodec, messageEnvelopeInput, messageKeyBoxAad,
    messageKeyBoxCodec, messagePayloadCodec, messageSendRequestCodec, parseJson, sha256, signDevice, validateDirectMessage, validateMessageDeliveryStatus,
    validateMessageEnvelope, validateMessageSendRequest, validateMessageTimelinePage, verifyMessageEnvelope, type JsonObject, type MessageEnvelope, type MessageSendRequest,
} from '@meshline/sdk';
import { vector, hex, unhex } from '../support/vectors.js';
import { messageDevice } from '../support/message-fixture.js';

const rows = vector<{
    network_context: string;
    envelope_signature: { private_key: string; signing_input_utf8_hex: string; signature: string };
    payload: { aad: { utf8_hex: string }; encryption: { envelope: JsonObject; recipient_boxes: JsonObject[]; recipient_private_key: string; recipient_public_key: string;
        ephemeral_private_key: string; ephemeral_public_key: string; shared_secret: string; hkdf_salt: string; wrap_key: string; sealed_key: string;
        content_key: string; plaintext_utf8_hex: string; ciphertext: string };
        aad_rejection_cases: { name: string; aad_overrides: JsonObject }[] };
    timeline: { request: JsonObject; plaintext_utf8_hex: string; content_key: string; hkdf_salt: string; message_aad_utf8_hex: string; envelope_signing_input_utf8_hex: string;
        sender_encryption_private_key: string; sender_signing_private_key: string; target_encryption_private_key: string;
        target_key_box: BoxVector; sender_key_box: BoxVector };
}>('message-encryption');
interface BoxVector { ephemeral_private_key: string; shared_secret: string; kdf_info_utf8_hex: string; aad_utf8_hex: string; wrapping_key: string; nonce: string; sealed_key: string }
const context = NetworkContext.parse(rows.network_context); const time = 1730000000;
const from = messageDevice(context, 1, 2); const to = messageDevice(context, 3, 4); const second = messageDevice(context, 3, 6);
const messageId = 'msg_AAECAwQFBgcICQoLDA0ODw';
const payload = { $type: 'meshline.message.direct', body: { content_type: 'text/plain', text: 'hello 中文😀' }, future: { signed: true } };
function options() { return { context, signer: from, messageId, createdAt: time, recipient: to.certificate.account, recipientDevices: [to.certificate, second.certificate], payload }; }
async function read(request: MessageSendRequest, receiver = to, index = 0) { return decryptMessage({ context, receiver, envelope: request.envelope, keyBox: request.recipientBoxes[index]!, sender: from.certificate }); }

test('message envelope signature and payload AAD match independent protocol bytes', () => {
    const envelope = messageEnvelopeCodec.decode(rows.payload.encryption.envelope);
    validateMessageEnvelope(envelope);
    expect(hex(messageAad(envelope, context))).toBe(rows.payload.aad.utf8_hex);
    const input = messageEnvelopeInput(envelope, context); expect(hex(input)).toBe(rows.envelope_signature.signing_input_utf8_hex);
    expect(signDevice(input, decodeBase64Url(rows.envelope_signature.private_key))).toEqual(decodeBase64Url(rows.envelope_signature.signature));
});

test.each(rows.payload.aad_rejection_cases)('message payload rejects independent AAD mutation: $name', row => {
    const original = parseJson(unhex(rows.payload.aad.utf8_hex)) as JsonObject;
    expect(() => decryptAes(decodeBase64Url(rows.payload.encryption.content_key), messageEnvelopeCodec.decode(rows.payload.encryption.envelope).payload.nonce,
        decodeBase64Url(rows.payload.encryption.ciphertext), canonicalBytes({ ...original, ...row.aad_overrides }))).toThrow();
});

test.each(['target', 'sender'] as const)('complete %s key box matches independently supplied X25519, HKDF, nonce and ciphertext', side => {
    const row = rows.timeline; const request = messageSendRequestCodec.decode(row.request); const fixture = side === 'target' ? row.target_key_box : row.sender_key_box;
    const account = side === 'target' ? request.envelope.to : request.envelope.from;
    const box = side === 'target' ? request.recipientBoxes[0]! : request.senderBoxes![0]!;
    const recipientKey = decodeBase64Url(side === 'target' ? row.target_encryption_private_key : row.sender_encryption_private_key);
    const ephemeral = decodeBase64Url(fixture.ephemeral_private_key); const shared = agreeKey(ephemeral, encryptionPublicKey(recipientKey));
    expect(encryptionPublicKey(ephemeral)).toEqual(box.enc); expect(shared).toEqual(decodeBase64Url(fixture.shared_secret));
    expect(agreeKey(recipientKey, box.enc)).toEqual(shared);
    const aad = messageKeyBoxAad(request.envelope, box, account, context); expect(hex(aad)).toBe(fixture.aad_utf8_hex); expect(hex(aad)).toBe(fixture.kdf_info_utf8_hex);
    const salt = sha256(encodeUtf8('Meshline/keybox-salt/v1')); expect(salt).toEqual(decodeBase64Url(row.hkdf_salt));
    const wrapping = deriveKey(shared, salt, aad); expect(wrapping).toEqual(decodeBase64Url(fixture.wrapping_key));
    const nonce = decodeBase64Url(fixture.nonce); const contentKey = decodeBase64Url(row.content_key);
    expect(concatBytes(nonce, encryptAes(wrapping, nonce, contentKey, aad))).toEqual(box.sealedKey);
    expect(decryptAes(wrapping, box.sealedKey.slice(0, 12), box.sealedKey.slice(12), aad)).toEqual(contentKey);
    expect(hex(messageAad(request.envelope, context))).toBe(row.message_aad_utf8_hex);
    expect(hex(messageEnvelopeInput(request.envelope, context))).toBe(row.envelope_signing_input_utf8_hex);
    expect(decryptAes(contentKey, request.envelope.payload.nonce, request.envelope.payload.ciphertext, messageAad(request.envelope, context))).toEqual(unhex(row.plaintext_utf8_hex));
    expect(messageSendRequestCodec.encode(request)).toEqual(row.request);
});

test('historical sender timeline vector decrypts through the full verified public API', async () => {
    const row = rows.timeline; const request = messageSendRequestCodec.decode(row.request);
    const certificate = deviceCertificateCodec.decode(row.request.signer_certificate!);
    const recovered: Uint8Array[] = [];
    const receiver = { certificate, async deriveSharedSecret(enc: Uint8Array) { const secret = agreeKey(decodeBase64Url(row.sender_encryption_private_key), enc); recovered.push(secret); return secret; } };
    expect(certificateId(certificate, context)).toBe(request.envelope.fromDeviceId);
    const result = await decryptMessage({ context, receiver, envelope: request.envelope, keyBox: request.senderBoxes![0]!, sender: certificate });
    expect(result).toEqual(parseJson(unhex(row.plaintext_utf8_hex))); expect(recovered[0]!.every(value => value === 0)).toBe(true);
});

test('all recipient and sender devices decrypt the same freshly generated immutable envelope', async () => {
    const request = await encryptMessage(options()); validateMessageSendRequest(request, time);
    expect(await read(request)).toEqual(payload); expect(await read(request, second, 1)).toEqual(payload);
    expect(await decryptMessage({ context, receiver: from, envelope: request.envelope, keyBox: request.senderBoxes![0]!, sender: from.certificate })).toEqual(payload);
    expect(messageKeyBoxCodec.stringify(request.recipientBoxes[0]!)).not.toBe(messageKeyBoxCodec.stringify(request.recipientBoxes[1]!));
    const next = await encryptMessage(options()); expect(next.envelope.payload.ciphertext).not.toEqual(request.envelope.payload.ciphertext);
});

test('self delivery omits sender boxes and authorization', async () => {
    const request = await encryptMessage({ ...options(), recipient: from.certificate.account, recipientDevices: [from.certificate] });
    expect(request.senderBoxes).toBeUndefined(); expect(request.authorization).toBeUndefined(); expect(await read(request, from)).toEqual(payload);
    await expect(encryptMessage({ ...options(), recipient: from.certificate.account, recipientDevices: [from.certificate], senderDevices: [from.certificate] })).rejects.toThrow('Self messages');
});

test('invalid signatures and account/device bindings fail before key agreement', async () => {
    const request = await encryptMessage(options()); const deriveSharedSecret = vi.fn(to.deriveSharedSecret);
    const args = { context, receiver: { ...to, deriveSharedSecret }, envelope: request.envelope, keyBox: request.recipientBoxes[0]!, sender: from.certificate };
    await expect(decryptMessage({ ...args, envelope: { ...request.envelope, deviceSignature: new Uint8Array(64) } })).rejects.toThrow('signature');
    await expect(decryptMessage({ ...args, keyBox: request.recipientBoxes[1]! })).rejects.toThrow('another');
    await expect(decryptMessage({ ...args, sender: to.certificate })).rejects.toThrow('another');
    await expect(decryptMessage({ ...args, envelope: { ...request.envelope, additionalProperties: { added: true } } })).rejects.toThrow('signature');
    expect(deriveSharedSecret).not.toHaveBeenCalled();
});

test('tampered boxes and authenticated ciphertext never return partial plaintext', async () => {
    const request = await encryptMessage(options()); const box = request.recipientBoxes[0]!;
    await expect(read({ ...request, recipientBoxes: [{ ...box, sealedKey: new Uint8Array(60) }] })).rejects.toThrow();
    await expect(read({ ...request, recipientBoxes: [{ ...box, enc: new Uint8Array(32) }] })).rejects.toThrow();
    let envelope: MessageEnvelope = { ...request.envelope, payload: { ...request.envelope.payload, ciphertext: new Uint8Array(19) } };
    envelope = { ...envelope, deviceSignature: signDevice(messageEnvelopeInput(envelope, context), from.signingKey) };
    await expect(read({ ...request, envelope })).rejects.toThrow('authentication');
    const zero = new Uint8Array(32);
    await expect(decryptMessage({ context, receiver: { certificate: to.certificate, deriveSharedSecret: async () => zero }, envelope: request.envelope, keyBox: box, sender: from.certificate })).rejects.toThrow('all-zero');
});

test('certificate duplication, expiry, wrong account and changed signer are rejected', async () => {
    await expect(encryptMessage({ ...options(), recipientDevices: [] })).rejects.toThrow('eight');
    await expect(encryptMessage({ ...options(), recipientDevices: [to.certificate, to.certificate] })).rejects.toThrow('Duplicate');
    await expect(encryptMessage({ ...options(), recipientDevices: [from.certificate] })).rejects.toThrow('another');
    await expect(encryptMessage({ ...options(), createdAt: to.certificate.expiresAt })).rejects.toThrow('validity');
    let certificate = from.certificate;
    const signer = { get certificate() { return certificate; }, async sign(input: Uint8Array) { certificate = to.certificate; return from.sign(input); } };
    await expect(encryptMessage({ ...options(), signer })).rejects.toThrow('changed identity');
});

test('encryption snapshots caller data before signing and erases random secrets even after cancellation', async () => {
    const original = structuredClone(payload); const recipients = [structuredClone(to.certificate)];
    const signer = { certificate: from.certificate, async sign(input: Uint8Array) { original.body.text = 'mutated'; recipients[0]!.encryptionPublicKey.fill(0); return from.sign(input); } };
    const request = await encryptMessage({ ...options(), payload: original, recipientDevices: recipients, signer }); expect(await read(request)).toEqual(payload);
    const randomBuffers: Uint8Array[] = []; const controller = new AbortController();
    const random = { bytes(length: number) { const bytes = new Uint8Array(length).fill(7); randomBuffers.push(bytes); return bytes; } };
    await expect(encryptMessage({ ...options(), random, signal: controller.signal, signer: { certificate: from.certificate, async sign(input: Uint8Array) { controller.abort(); return from.sign(input); } } })).rejects.toThrow();
    expect(randomBuffers[0]!.every(value => value === 0)).toBe(true);
});

test('message size counts extensions and UTF-8 bytes; unknown complete payloads retain exact types', async () => {
    const request = await encryptMessage(options()); const base = { ...request.envelope, additionalProperties: { future: '' } };
    const length = encodeUtf8(messageEnvelopeCodec.stringify(base)).length;
    validateMessageEnvelope({ ...base, additionalProperties: { future: 'x'.repeat(262144 - length) } });
    expect(() => validateMessageEnvelope({ ...base, additionalProperties: { future: 'x'.repeat(262145 - length) } })).toThrow('262144');
    expect(messagePayloadCodec.decode({ $type: 'com.example.unknown', value: true })).toEqual({ $type: 'com.example.unknown', value: true });
    for (const value of [{ $type: 3 }, { arbitrary: true }, { $type: 'future', value: null }]) expect(() => messagePayloadCodec.decode(value)).toThrow();
});

test('direct messages, delivery outcomes, and timeline pages enforce their semantic invariants', async () => {
    const request = await encryptMessage(options()); const direct = directMessageCodec.decode(payload); validateDirectMessage(direct);
    expect(canonicalJson(directMessageCodec.encode(direct))).toBe(canonicalJson(payload));
    expect(() => validateDirectMessage({})).toThrow(); expect(() => validateDirectMessage({ body: { contentType: 'text/plain', text: '\u0085' } })).toThrow();
    expect(() => validateDirectMessage({ ...direct, replyTo: { from: to.certificate.account, messageId: 'wrong' } })).toThrow();
    validateMessageDeliveryStatus({ status: 'delivering', acceptedAt: time });
    expect(() => validateMessageDeliveryStatus({ status: 'failed', acceptedAt: time })).toThrow();
    expect(() => validateMessageDeliveryStatus({ status: 'target_accepted', acceptedAt: time, error: { code: 'internal_error', message: 'bad' } })).toThrow();
    const entry = { sequence: 3, envelope: request.envelope, keyBox: request.recipientBoxes[0]!, acceptedAt: time };
    const page = { items: [entry], certificates: [from.certificate], hasMore: false };
    validateMessageTimelinePage(page, context, 1);
    expect(() => validateMessageTimelinePage(page, context, 3)).toThrow('strictly');
    expect(() => validateMessageTimelinePage({ ...page, items: [], hasMore: true }, context)).toThrow('empty');
    expect(() => validateMessageTimelinePage({ ...page, certificates: [] }, context)).toThrow('certificate');
    expect(() => validateMessageTimelinePage({ ...page, certificates: [from.certificate, from.certificate] }, context)).toThrow('duplicate');
    verifyMessageEnvelope(request.envelope, from.certificate, context);
});
