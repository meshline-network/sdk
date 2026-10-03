import * as sdk from '@meshline/sdk';

export interface VectorFile { readonly source: string; readonly sha256: string }
export interface VectorResult { readonly name: string; readonly passed: boolean; readonly error?: string }
export interface VectorReport { readonly results: readonly VectorResult[]; readonly passed: number; readonly failed: number }
const topics = ['common', 'identity-auth', 'message-encryption', 'message-content', 'contacts', 'channels', 'groups'] as const;
type Topic = typeof topics[number];
const object = (value: sdk.JsonValue | undefined): sdk.JsonObject => sdk.requireObject(value!);
function rows(value: sdk.JsonValue | undefined): sdk.JsonObject[] { if (!Array.isArray(value)) throw new Error('Vector array is absent.'); return value.map(object); }
function text(value: sdk.JsonValue | undefined): string { if (typeof value !== 'string') throw new Error('Vector string is absent.'); return value; }
function number(value: sdk.JsonValue | undefined): number { if (typeof value !== 'number') throw new Error('Vector number is absent.'); return value; }
function hex(bytes: Uint8Array): string { return Array.from(bytes, value => value.toString(16).padStart(2, '0')).join(''); }
function unhex(value: sdk.JsonValue | undefined): Uint8Array { const input = text(value); if (!/^(?:[0-9a-f]{2})*$/i.test(input)) throw new Error('Vector hex is invalid.'); return Uint8Array.from(input.match(/../g) ?? [], value => parseInt(value, 16)); }
const bytes = (value: sdk.JsonValue | undefined) => sdk.decodeBase64Url(text(value));
function equal(actual: sdk.JsonValue | Uint8Array | undefined, expected: sdk.JsonValue | Uint8Array | undefined): void {
    const encode = (value: typeof actual) => value instanceof Uint8Array ? hex(value) : value === undefined ? 'undefined' : sdk.canonicalJson(value);
    if (encode(actual) !== encode(expected)) throw new Error('Result differs from the independent expected value.');
}
function rejects(action: () => unknown): void { try { action(); } catch { return; } throw new Error('Invalid input was accepted.'); }
function fixedRandom(...values: (sdk.JsonValue | undefined)[]): sdk.RandomSource {
    const queue = values.map(bytes); return { bytes(length) { const value = queue.shift(); if (value?.length !== length) throw new Error('Unexpected vector random request.'); return value; } };
}

/** Offline vector subset. No Node, test-framework, native-module or network dependencies.
 * Caller supplies a UI yield between checks; failures are collected, never converted to passes.
 */
export async function runPortableVectors(files: Readonly<Record<string, VectorFile>>, onResult?: (result: VectorResult) => void,
    yieldBetween: () => Promise<void> = () => Promise.resolve(), random: sdk.RandomSource = sdk.systemRandom): Promise<VectorReport> {
    const results: VectorResult[] = []; const documents = {} as Record<Topic, sdk.JsonObject>;
    async function check(name: string, action: () => void | Promise<void>): Promise<void> {
        await yieldBetween(); let result: VectorResult;
        try { await action(); result = { name, passed: true }; }
        catch (error) { result = { name, passed: false, error: error instanceof Error ? error.message : String(error) }; }
        results.push(result); onResult?.(result);
    }
    const report = (): VectorReport => ({ results, passed: results.filter(value => value.passed).length, failed: results.filter(value => !value.passed).length });
    for (const topic of topics) await check(`fixture SHA-256: ${topic}`, () => {
        const file = files[`${topic}-v1.json`]; if (!file) throw new Error('Required fixture is absent.');
        equal(hex(sdk.sha256(sdk.encodeUtf8(file.source))), file.sha256);
        // Fixture metadata includes intentionally invalid protocol values. The SDK
        // parser is exercised on each protocol input, not on its test container.
        documents[topic] = object(JSON.parse(file.source) as sdk.JsonValue);
    });
    if (results.some(value => !value.passed)) return report();
    const common = documents.common; const identity = documents['identity-auth']; const message = documents['message-encryption'];
    const context = sdk.NetworkContext.parse(text(common.network_context));
    const canonical = object(common.canonical_json);
    for (const row of rows(canonical.vectors)) await check(`canonical JSON: ${text(row.id)}`, () => {
        const parsed = sdk.parseJson(text(row.input_json)); const expected = object(row.expected);
        const output = row.operation === 'network_bound_json' ? sdk.signingInput(object(parsed), context) : sdk.canonicalBytes(parsed);
        equal(sdk.decodeUtf8(output), expected.canonical_json); equal(hex(output), expected.utf8_hex); equal(hex(sdk.sha256(output)), expected.sha256_hex);
    });
    for (const row of rows(canonical.rejection_vectors)) await check(`JSON rejection: ${text(row.id)}`, () => rejects(() => sdk.canonicalJson(sdk.parseJson(text(row.input_json)))));
    for (const [index, row] of rows(object(common.base64url).cases).entries()) await check(`base64url: ${index}`, () => {
        const decode = () => { const prefix = row.prefix === undefined ? '' : text(row.prefix); const input = text(row.input);
            if (!input.startsWith(prefix)) throw new Error('Wrong identifier prefix.');
            return sdk.decodeBase64Url(input.slice(prefix.length), prefix === 'sha256:' ? 32 : prefix ? 16 : undefined); };
        if (row.expected_accepted) { const output = decode(); equal(hex(output), row.decoded_hex); equal((row.prefix ?? '') + sdk.encodeBase64Url(output), row.input); }
        else rejects(decode);
    });
    for (const [index, row] of rows(object(common.network_binding).format_cases).entries()) await check(`network context: ${index}`, () => {
        const parse = () => sdk.NetworkContext.parse(text(row.input)); if (row.expected_accepted) equal(parse().toString(), row.input); else rejects(parse);
    });
    for (const row of rows(object(common.x25519).cases)) await check(`X25519: ${text(row.name)}`, () => {
        const agree = () => sdk.agreeKey(bytes(row.private_key), bytes(row.peer_public_key)); if (row.expected_accepted) equal(agree(), bytes(row.expected_raw_shared_secret)); else rejects(agree);
    });
    for (const row of rows(object(identity.relay_origin).normalization_cases)) await check(`relay origin: ${text(row.name)}`, () => equal(sdk.relayOrigin(text(row.endpoint)), row.expected_origin));
    for (const row of rows(object(identity.relay_origin).comparison_cases)) await check(`origin comparison: ${text(row.name)}`, () => equal(sdk.relayOrigin(text(row.left_endpoint)) === sdk.relayOrigin(text(row.right_endpoint)), row.expected_same_origin));
    await check('Neo identity and nested P-256 / Ed25519 certificate signatures', () => {
        const row = object(identity.device_certificate); const wire = { ...object(row.unsigned_object), device_signature: row.device_signature!, account_signature: row.account_signature! };
        const certificate = sdk.deviceCertificateCodec.decode(wire); sdk.validateCertificate(certificate, context);
        equal(sdk.accountPublicKey(bytes(row.private_key)), bytes(row.public_key)); equal(sdk.devicePublicKey(bytes(row.device_private_key)), bytes(row.device_public_key));
        equal(sdk.getAccountId(`neo:${context.reference}`, bytes(row.public_key)), row.account); equal(sdk.certificateId(certificate, context), object(identity.device_identity).derived_device_id);
        equal(hex(sdk.certificateDeviceInput(certificate, context)), row.device_signing_input_utf8_hex);
        equal(sdk.signDevice(sdk.certificateDeviceInput(certificate, context), bytes(row.device_private_key)), bytes(row.device_signature));
        const input = sdk.certificateAccountInput(certificate, context); equal(hex(input), row.account_signing_input_utf8_hex);
        equal(sdk.verifyAccount(input, sdk.signAccount(input, bytes(row.private_key), random), bytes(row.public_key)), true);
        rejects(() => sdk.validateCertificate(certificate, new sdk.NetworkContext(context.reference + 1, context.registry)));
    });
    const timeline = object(message.timeline); const request = sdk.messageSendRequestCodec.decode(timeline.request!);
    for (const side of ['target', 'sender'] as const) await check(`direct message ${side} X25519 / HKDF / AES-GCM key box`, () => {
        const row = object(timeline[`${side}_key_box`]); const box = side === 'target' ? request.recipientBoxes[0]! : request.senderBoxes![0]!;
        const account = side === 'target' ? request.envelope.to : request.envelope.from;
        const shared = sdk.agreeKey(bytes(timeline[`${side}_encryption_private_key`]), box.enc); equal(shared, bytes(row.shared_secret));
        const aad = sdk.messageKeyBoxAad(request.envelope, box, account, context); equal(hex(aad), row.aad_utf8_hex); equal(hex(aad), row.kdf_info_utf8_hex);
        const wrapping = sdk.deriveKey(shared, bytes(timeline.hkdf_salt), aad); equal(wrapping, bytes(row.wrapping_key));
        equal(sdk.decryptAes(wrapping, box.sealedKey.slice(0, 12), box.sealedKey.slice(12), aad), bytes(timeline.content_key));
        equal(sdk.concatBytes(bytes(row.nonce), sdk.encryptAes(wrapping, bytes(row.nonce), bytes(timeline.content_key), aad)), box.sealedKey);
        const altered = box.sealedKey.slice(12); altered[0] = altered[0]! ^ 1; rejects(() => sdk.decryptAes(wrapping, box.sealedKey.slice(0, 12), altered, aad));
    });
    await check('direct message payload and envelope signature', () => {
        const aad = sdk.messageAad(request.envelope, context); equal(hex(aad), timeline.message_aad_utf8_hex);
        equal(sdk.decryptAes(bytes(timeline.content_key), request.envelope.payload.nonce, request.envelope.payload.ciphertext, aad), unhex(timeline.plaintext_utf8_hex));
        const input = sdk.messageEnvelopeInput(request.envelope, context); equal(hex(input), timeline.envelope_signing_input_utf8_hex);
        equal(sdk.signDevice(input, bytes(timeline.sender_signing_private_key)), request.envelope.deviceSignature);
    });
    for (const row of rows(object(message.payload).aad_rejection_cases)) await check(`direct message AAD rejection: ${text(row.name)}`, () => {
        const payload = object(message.payload); const encryption = object(payload.encryption); const original = object(sdk.parseJson(unhex(object(payload.aad).utf8_hex)));
        rejects(() => sdk.decryptAes(bytes(encryption.content_key), bytes(encryption.payload_nonce), bytes(encryption.ciphertext), sdk.canonicalBytes({ ...original, ...object(row.aad_overrides) })));
    });
    const content = documents['message-content']; const body = object(content.body);
    for (const row of rows(body.body_cases)) await check(`message body: ${text(row.name)}`, () => sdk.validateMessageBody(sdk.messageBodyCodec.decode(row.body!)));
    for (const row of rows(body.invalid_body_cases)) await check(`body rejection: ${text(row.name)}`, () => rejects(() => sdk.validateMessageBody(sdk.messageBodyCodec.decode(row.body!))));
    for (const row of rows(object(content.hash_references).hash_cases)) await check(`content hash: ${text(row.name)}`, () => {
        const decode = () => sdk.contentHashBytes(text(row.value)); if (row.valid) equal(decode().length, 32); else rejects(decode);
    });
    await check('attachment AAD, encryption, decryption and content integrity', () => {
        const row = object(content.attachment_encryption); const reference = sdk.contentReferenceCodec.decode(row.reference!); const plaintext = unhex(row.plaintext_utf8_hex); const ciphertext = bytes(row.ciphertext_and_tag);
        equal(hex(sdk.contentEncryptionAad(reference)), row.aad_utf8_hex); equal(sdk.encryptContent(reference, plaintext), ciphertext); equal(sdk.decryptContent(reference, ciphertext), plaintext);
        rejects(() => sdk.decryptContent({ ...reference, contentType: 'image/png' }, ciphertext));
    });
    const contacts = documents.contacts; const grant = object(object(contacts.signing).vector);
    await check('contact grant signed bytes', () => {
        const input = sdk.contactGrantInput(sdk.contactGrantCodec.decode(grant.grant!), context); equal(hex(input), grant.signing_input_utf8_hex); equal(sdk.signDevice(input, bytes(grant.private_key)), bytes(grant.signature));
    });
    for (const row of rows(object(contacts.signing).verification_cases)) await check(`contact signature: ${text(row.name)}`, () => {
        const document = { ...object(grant.grant), ...(row.grant_overrides ? object(row.grant_overrides) : {}) };
        for (const field of row.grant_remove_fields as string[] ?? []) delete document[field];
        const network = row.network_context ? sdk.NetworkContext.parse(text(row.network_context)) : context;
        equal(sdk.verifyDevice(sdk.signingInput(document, network, ['signatures']), bytes(row.signature ?? grant.signature), bytes(row.public_key ?? grant.public_key)), row.expected_signature_valid);
    });
    for (const row of rows(object(contacts.grants).format_cases)) await check(`contact grant format: ${text(row.name)}`, () => {
        const validate = () => sdk.validateContactGrant(sdk.contactGrantCodec.parse(text(row.input_json)), number(object(contacts.grants).verification_time));
        if (row.expected === 'accept') validate(); else rejects(validate);
    });
    const channels = documents.channels;
    for (const kind of ['channel', 'group'] as const) await check(`${kind} resource identifier`, () => {
        const row = object((kind === 'channel' ? channels : documents.groups)[`${kind}_id`]); const input = object(row.input);
        equal(sdk.deriveResourceId(kind, text(input.creator), text(input.relay_id), bytes(input.nonce), context), object(row.expected)[`${kind}_id`]);
    });
    const writes = rows(channels.channel_write_cases); const channelKey = bytes(object(channels.channel_signer).private_key);
    for (const row of writes) await check(`channel write: ${text(row.name)}`, () => {
        const payload = sdk.channelPayloadCodec.decode(row.request!); sdk.validateChannelPayload(payload, context); const input = sdk.channelPayloadInput(payload, context);
        equal(hex(input), row.signing_input_utf8_hex); equal(sdk.signDevice(input, channelKey), bytes(object(row.request).device_signature)); equal(sdk.channelPayloadCodec.encode(payload), row.request);
    });
    for (const row of rows(channels.channel_edit_sequences)) await check(`channel edits: ${text(row.name)}`, () => {
        let post = sdk.channelPostCodec.decode(writes.find(value => value.name === row.post_case)!.request!);
        for (const step of rows(row.steps)) {
            const document = object(step.request); const input = sdk.signingInput(document, context, ['device_signature']); equal(hex(input), step.signing_input_utf8_hex); equal(sdk.signDevice(input, channelKey), bytes(document.device_signature));
            const apply = () => sdk.applyChannelPostEdit(post, sdk.channelPostEditCodec.decode(document)); const expected = object(step.expected);
            if (expected.status === 'bad_request') rejects(apply); else { post = apply(); const wire = sdk.channelPostCodec.encode(post); equal(wire.body, expected.body); equal(wire.attachments, expected.attachments); }
        }
    });
    const keying = object(documents.groups.keying); const groupInput = object(keying.input); const groupExpected = object(keying.expected);
    await check('group split secrets, commitment, X25519 boxes and HKDF', () => {
        const clientSecret = bytes(groupInput.client_group_secret); const relaySecret = bytes(groupInput.relay_epoch_secret);
        const groupId = text(groupInput.group_id); const account = text(groupInput.account); const epoch = number(groupInput.epoch);
        equal(sdk.groupClientSecretCommitment(groupId, clientSecret, context), groupExpected.client_secret_commitment);
        const header = { groupId, account, memberEncryptionPublicKey: bytes(groupInput.member_encryption_public_key), clientSecretCommitment: text(groupExpected.client_secret_commitment) };
        const box = sdk.sealGroupClientSecret(header, clientSecret, context, fixedRandom(groupInput.client_box_ephemeral_private_key, groupInput.client_box_nonce)); equal(sdk.groupSecretBoxCodec.encode(box), groupExpected.client_secret_box);
        equal(sdk.openGroupSecret(box, sdk.agreeKey(bytes(groupInput.member_encryption_private_key), box.enc), sdk.groupClientSecretBoxAad(header, context)), clientSecret);
        const aad = sdk.groupRelaySecretBoxAad({ groupId, account, epoch, deviceId: text(groupInput.device_id) }, context);
        const relayBox = sdk.sealGroupSecret(relaySecret, bytes(groupInput.device_encryption_public_key), aad, fixedRandom(groupInput.relay_box_ephemeral_private_key, groupInput.relay_box_nonce)); equal(sdk.groupSecretBoxCodec.encode(relayBox), groupExpected.relay_secret_box);
        equal(sdk.openGroupSecret(relayBox, sdk.agreeKey(bytes(groupInput.device_encryption_private_key), relayBox.enc), aad), relaySecret);
        equal(sdk.deriveGroupApplicationSecret(groupId, epoch, text(groupExpected.client_secret_commitment), clientSecret, relaySecret, context), bytes(groupExpected.epoch_application_secret));
    });
    await check('group message AAD, ciphertext and signature', () => {
        const envelope = sdk.groupMessageEnvelopeCodec.decode(groupExpected.message!); const aad = sdk.groupMessageAad(envelope, text(groupInput.account), text(groupInput.device_id), context);
        equal(hex(aad), groupExpected.message_aad_utf8_hex); const key = sdk.deriveGroupMessageKey(bytes(groupExpected.epoch_application_secret), aad); equal(key, bytes(groupExpected.message_key));
        const plaintext = sdk.encodeUtf8(JSON.stringify(groupInput.message_content)); equal(sdk.decryptAes(key, envelope.payload.nonce, envelope.payload.ciphertext, aad), plaintext);
        equal(sdk.encryptAes(key, envelope.payload.nonce, plaintext, aad), envelope.payload.ciphertext); equal(sdk.signDevice(sdk.groupMessageEnvelopeInput(envelope, context), bytes(groupInput.message_signing_private_key)), envelope.deviceSignature);
    });
    return report();
}
