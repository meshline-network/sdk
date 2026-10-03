import { expect, test } from 'vitest';
import {
    NetworkContext, agreeKey, canonicalBytes, certificateId, decodeBase64Url, decryptAes, decryptGroupMessage, deriveGroupApplicationSecret, deriveGroupMessageKey,
    deviceCertificateCodec, encodeBase64Url, encodeUtf8, encryptAes, encryptGroupMessage, groupClientSecretBoxAad, groupClientSecretCommitment, groupManagementHash,
    groupMessageCodec, groupMessageEnvelopeCodec, groupMessageEnvelopeInput, groupMessageAad, groupMemberNicknameUpdateCodec, groupPayloadCodec,
    groupRelaySecretBoxAad, groupSecretBoxCodec, openGroupSecret, sealGroupClientSecret, sealGroupSecret, signDevice, validateGroupPayload,
    validateGroupMessage, validateGroupMemberNicknameUpdate, verifyGroupMessageEnvelope, validateGroupMessageEnvelope,
    accountGroupHistorySecretSyncCodec, accountGroupPrivateStateSyncCodec, accountGroupPrivateStateRequestCodec,
    validateAccountGroupHistorySecretSync, validateAccountGroupPrivateStateSync, validateAccountGroupPrivateStateRequest,
    type JsonObject, type JsonValue, type RandomSource,
} from '@meshline/sdk';
import { vector, hex, unhex } from '../support/vectors.js';

interface Actor { label: string; signing_private_key: string; certificate: JsonObject; device_id: string }
interface NicknameVector { name: string; actor: string; plaintext: JsonObject; plaintext_utf8_hex: string; message_aad_utf8_hex: string; signing_input_utf8_hex: string;
    event: { epoch: number; signer_device_id: string; payload: JsonObject }; expected_sender_account: string; expected_business_valid: boolean | null; message_nonce: string; message_key: string }
const data = vector<{
    network_context: string;
    keying: { input: { account: string; group_id: string; device_id: string; epoch: number; member_encryption_private_key: string; member_encryption_public_key: string;
        device_encryption_private_key: string; device_encryption_public_key: string; client_group_secret: string; relay_epoch_secret: string; client_box_ephemeral_private_key: string; client_box_nonce: string;
        relay_box_ephemeral_private_key: string; relay_box_nonce: string; message_signing_private_key: string; message_id: string; message_created_at: number; message_content: JsonObject; message_nonce: string };
        expected: { client_secret_commitment: string; epoch_application_secret: string; client_secret_box: JsonObject; relay_secret_box: JsonObject; message_aad_utf8_hex: string; message: JsonObject; message_key: string } };
    management_chain: { group_id: string; relay_id: string; actors: Actor[]; signer_account_binding: { cases: { name: string; certificate: JsonObject; signer_device_id: string }[] };
        chain: { event: { payload: JsonObject }; management_hash?: string }[] };
    encrypted_nicknames: { epoch_inputs: { epoch: number; client_group_secret: string; relay_epoch_secret: string; client_secret_commitment: string; epoch_application_secret: string }[];
        messages: NicknameVector[]; field_cases: { name: string; omit?: boolean; value?: JsonValue; expected_valid: boolean }[] };
}>('groups');
const context = NetworkContext.parse(data.network_context); const { input, expected } = data.keying;
function deterministic(...values: string[]): RandomSource { return { bytes(length) { const value = decodeBase64Url(values.shift()!); expect(value.length).toBe(length); return value; } }; }
const applicationSecret = decodeBase64Url(expected.epoch_application_secret);
test('independent split-secret commitment, client/relay key boxes and epoch key agree byte for byte', () => {
    const clientSecret = decodeBase64Url(input.client_group_secret); const relaySecret = decodeBase64Url(input.relay_epoch_secret);
    expect(groupClientSecretCommitment(input.group_id, clientSecret, context)).toBe(expected.client_secret_commitment);
    const clientHeader = { groupId: input.group_id, account: input.account, memberEncryptionPublicKey: decodeBase64Url(input.member_encryption_public_key), clientSecretCommitment: expected.client_secret_commitment };
    const clientBox = sealGroupClientSecret(clientHeader, clientSecret, context, deterministic(input.client_box_ephemeral_private_key, input.client_box_nonce));
    expect(groupSecretBoxCodec.encode(clientBox)).toEqual(expected.client_secret_box);
    const shared = agreeKey(decodeBase64Url(input.member_encryption_private_key), clientBox.enc);
    expect(openGroupSecret(clientBox, shared, groupClientSecretBoxAad(clientHeader, context))).toEqual(clientSecret);
    const relayAad = groupRelaySecretBoxAad({ groupId: input.group_id, account: input.account, deviceId: input.device_id, epoch: input.epoch }, context);
    const relayBox = sealGroupSecret(relaySecret, decodeBase64Url(input.device_encryption_public_key), relayAad, deterministic(input.relay_box_ephemeral_private_key, input.relay_box_nonce));
    expect(groupSecretBoxCodec.encode(relayBox)).toEqual(expected.relay_secret_box);
    expect(openGroupSecret(relayBox, agreeKey(decodeBase64Url(input.device_encryption_private_key), relayBox.enc), relayAad)).toEqual(relaySecret);
    expect(deriveGroupApplicationSecret(input.group_id, input.epoch, expected.client_secret_commitment, clientSecret, relaySecret, context)).toEqual(applicationSecret);
    expect(() => deriveGroupApplicationSecret(input.group_id, input.epoch, expected.client_secret_commitment, relaySecret, clientSecret, context)).toThrow('commitment');
    expect(() => openGroupSecret(clientBox, shared, relayAad)).toThrow('authentication'); expect(() => openGroupSecret(clientBox, new Uint8Array(32), relayAad)).toThrow('all-zero');
    expect(() => sealGroupSecret(clientSecret, new Uint8Array(32), relayAad)).toThrow('agreement');
    expect(clientSecret).toEqual(decodeBase64Url(input.client_group_secret)); expect(relaySecret).toEqual(decodeBase64Url(input.relay_epoch_secret));
});
test('independent group envelope, AAD, message key, ciphertext and signature agree including encrypted attachments', () => {
    const envelope = groupMessageEnvelopeCodec.decode(expected.message); validateGroupMessageEnvelope(envelope); validateGroupMessage(groupMessageCodec.decode(input.message_content));
    const aad = groupMessageAad(envelope, input.account, input.device_id, context); expect(hex(aad)).toBe(expected.message_aad_utf8_hex);
    const key = deriveGroupMessageKey(applicationSecret, aad); expect(encodeBase64Url(key)).toBe(expected.message_key);
    // The independent vector encrypts insertion-order JSON. Plaintext need not be in signing-input canonical order.
    const plaintext = encodeUtf8(JSON.stringify(input.message_content));
    expect(encryptAes(key, decodeBase64Url(input.message_nonce), plaintext, aad)).toEqual(envelope.payload.ciphertext);
    expect(decryptAes(key, envelope.payload.nonce, envelope.payload.ciphertext, aad)).toEqual(plaintext);
    expect(signDevice(groupMessageEnvelopeInput(envelope, context), decodeBase64Url(input.message_signing_private_key))).toEqual(envelope.deviceSignature);
    expect(groupMessageEnvelopeCodec.encode(envelope)).toEqual(expected.message);
});
test.each(data.encrypted_nicknames.epoch_inputs)('nickname epoch $epoch uses both independent secret halves', row => {
    expect(encodeBase64Url(deriveGroupApplicationSecret(data.management_chain.group_id, row.epoch, row.client_secret_commitment, decodeBase64Url(row.client_group_secret), decodeBase64Url(row.relay_epoch_secret), context))).toBe(row.epoch_application_secret);
});
function actorFor(row: NicknameVector) {
    const actor = data.management_chain.actors.find(value => value.label === (row.actor === 'A2' ? 'A' : row.actor))!;
    const certificate = deviceCertificateCodec.decode(row.actor === 'A2' ? data.management_chain.signer_account_binding.cases.find(value => value.name === 'same_account')!.certificate : actor.certificate);
    return { certificate, async sign(bytes: Uint8Array) { return signDevice(bytes, decodeBase64Url(actor.signing_private_key)); } };
}
test.each(data.encrypted_nicknames.messages)('encrypted nickname vector: $name', async row => {
    const envelope = groupMessageEnvelopeCodec.decode(row.event.payload); const signer = actorFor(row);
    const secret = decodeBase64Url(data.encrypted_nicknames.epoch_inputs.find(value => value.epoch === envelope.epoch)!.epoch_application_secret);
    expect(signer.certificate.account).toBe(row.expected_sender_account); expect(certificateId(signer.certificate, context)).toBe(row.event.signer_device_id);
    const aad = groupMessageAad(envelope, signer.certificate.account, row.event.signer_device_id, context);
    expect(hex(aad)).toBe(row.message_aad_utf8_hex); expect(encodeBase64Url(deriveGroupMessageKey(secret, aad))).toBe(row.message_key);
    expect(hex(groupMessageEnvelopeInput(envelope, context))).toBe(row.signing_input_utf8_hex);
    const plaintext = decryptGroupMessage({ context, envelope, sender: signer.certificate, signerDeviceId: row.event.signer_device_id, applicationSecret: secret });
    expect(plaintext).toEqual(row.plaintext);
    expect(decryptAes(decodeBase64Url(row.message_key), envelope.payload.nonce, envelope.payload.ciphertext, aad)).toEqual(unhex(row.plaintext_utf8_hex));
    if (row.expected_business_valid === false) { expect(() => validateGroupPayload(plaintext)).toThrow(); return; }
    validateGroupPayload(plaintext);
    const encrypted = await encryptGroupMessage({ context, signer, groupId: envelope.groupId, epoch: envelope.epoch, messageId: envelope.messageId, createdAt: envelope.createdAt,
        applicationSecret: secret, payload: plaintext, random: deterministic(row.message_nonce) });
    if (hex(canonicalBytes(plaintext)) === row.plaintext_utf8_hex) expect(groupMessageEnvelopeCodec.encode(encrypted)).toEqual(row.event.payload);
    else expect(decryptGroupMessage({ context, envelope: encrypted, sender: signer.certificate, signerDeviceId: row.event.signer_device_id, applicationSecret: secret })).toEqual(row.plaintext);
});
test.each(data.encrypted_nicknames.field_cases)('nickname Unicode, byte length and presence: $name', row => {
    const run = () => validateGroupMemberNicknameUpdate(groupMemberNicknameUpdateCodec.decode({ $type: 'meshline.group.member.nickname.update', ...(row.omit ? {} : { nickname: row.value! }) }));
    if (row.expected_valid) expect(run).not.toThrow(); else expect(run).toThrow();
});
test('group message authentication rejects same signing key under a different account or device certificate', () => {
    const row = data.encrypted_nicknames.messages[0]!; const envelope = groupMessageEnvelopeCodec.decode(row.event.payload);
    const secret = decodeBase64Url(data.encrypted_nicknames.epoch_inputs.find(value => value.epoch === envelope.epoch)!.epoch_application_secret);
    for (const binding of data.management_chain.signer_account_binding.cases) {
        const sender = deviceCertificateCodec.decode(binding.certificate);
        // Both alternate certificates deliberately hold the same signing key: signature verification alone is insufficient.
        verifyGroupMessageEnvelope(envelope, sender, binding.signer_device_id, context);
        expect(() => decryptGroupMessage({ context, envelope, sender, signerDeviceId: binding.signer_device_id, applicationSecret: secret })).toThrow('authentication');
        expect(() => decryptGroupMessage({ context, envelope, sender, signerDeviceId: row.event.signer_device_id, applicationSecret: secret })).toThrow('another device');
    }
    const sender = actorFor(row).certificate; const options = { context, envelope, sender, signerDeviceId: row.event.signer_device_id, applicationSecret: secret };
    const tampered = groupMessageEnvelopeCodec.decode(row.event.payload); tampered.payload.ciphertext[0]! ^= 1;
    expect(() => decryptGroupMessage({ ...options, envelope: tampered })).toThrow('signature');
    expect(() => decryptGroupMessage({ ...options, applicationSecret: new Uint8Array(32) })).toThrow('authentication');
});
test('group payload null exception is limited to nickname deletion and does not normalize or discard extensions', () => {
    const clear = { $type: 'meshline.group.member.nickname.update', nickname: null, future: { enabled: true } };
    expect(groupPayloadCodec.decode(clear)).toEqual(clear); validateGroupPayload(clear);
    expect(() => validateGroupPayload({ ...clear, future: { enabled: null } })).toThrow('Null');
    expect(() => validateGroupPayload({ $type: 'meshline.group.future', nickname: null })).toThrow('Null');
    expect(() => validateGroupMessage({ body: { contentType: 'text/plain', text: 'text' }, replyToSeq: 0 })).toThrow();
});
test('account-only group key synchronization validates exact identities, unique versions and mandatory nonempty batches', () => {
    const secret = { group_id: input.group_id, epoch: input.epoch, application_secret: expected.epoch_application_secret };
    const sync = { $type: 'meshline.account.group.history_secret.sync', secrets: [secret] };
    validateAccountGroupHistorySecretSync(accountGroupHistorySecretSyncCodec.decode(sync));
    expect(() => validateAccountGroupHistorySecretSync(accountGroupHistorySecretSyncCodec.decode({ ...sync, secrets: [secret, secret] }))).toThrow('repeats');
    expect(() => validateAccountGroupHistorySecretSync(accountGroupHistorySecretSyncCodec.decode({ ...sync, secrets: [] }))).toThrow('at least');
    const state = { group_id: input.group_id, relay_id: data.management_chain.relay_id, member_encryption_private_key: input.member_encryption_private_key };
    const batch = { $type: 'meshline.account.group.state.sync', states: [state] }; validateAccountGroupPrivateStateSync(accountGroupPrivateStateSyncCodec.decode(batch));
    expect(() => validateAccountGroupPrivateStateSync(accountGroupPrivateStateSyncCodec.decode({ ...batch, states: [state, state] }))).toThrow('repeats');
    expect(() => validateAccountGroupPrivateStateSync(accountGroupPrivateStateSyncCodec.decode({ ...batch, states: [] }))).toThrow('at least');
    const request = { $type: 'meshline.account.group.state.request' }; validateAccountGroupPrivateStateRequest(accountGroupPrivateStateRequestCodec.decode(request));
    expect(() => accountGroupPrivateStateRequestCodec.decode({ ...request, group_id: null })).toThrow();
});
test('management hashes include signatures and exclude external timeline metadata', () => {
    for (const row of data.management_chain.chain) if (row.management_hash !== undefined) expect(groupManagementHash(row.event.payload, context)).toBe(row.management_hash);
    const first = data.management_chain.chain[0]!;
    expect(groupManagementHash({ ...first.event.payload, device_signature: encodeBase64Url(new Uint8Array(64)) }, context)).not.toBe(first.management_hash);
});
