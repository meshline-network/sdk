import { afterAll, beforeAll, expect, test } from 'vitest';
import {
    NetworkContext, accountPublicKey, canonicalJson, certificateAccountInput, certificateDeviceInput,
    certificateId, decodeBase64Url, decryptAes, deviceCertificateCodec, devicePublicKey, encodeBase64Url,
    encodeUtf8, encryptAes, getAccountId, getRelayId, parseJson, signAccount, signDevice,
    validateCertificate, verifyAccount, verifyDevice, type JsonObject,
} from '../../../typescript/packages/sdk/src/index.js';
import { DotnetBridge } from '../../../typescript/tests/support/dotnet.js';
import { vector } from '../../../typescript/tests/support/vectors.js';
import { context as relayContext, signedDescriptor, peerId } from '../../../typescript/tests/support/relay-fixture.js';
import { relayDescriptorCodec, validateRelayDescriptor } from '../../../typescript/packages/sdk/src/index.js';
import { accountProfileCodec, profileInput, profileResolveResultCodec, validateProfileResult } from '../../../typescript/packages/sdk/src/index.js';
import { encryptMessage, decryptMessage, messageSendRequestCodec, messageEnvelopeCodec, messageKeyBoxCodec } from '../../../typescript/packages/sdk/src/index.js';
import { messageDevice } from '../../../typescript/tests/support/message-fixture.js';
import { channelFixture } from '../../../typescript/tests/support/channel-fixture.js';
import { channelPayloadCodec, channelPayloadInput, channelEventCodec, channelPostCodec, applyChannelPostEdit, verifyChannelDescriptor } from '@meshline/sdk';
import { encryptGroupMessage, decryptGroupMessage, groupMessageEnvelopeCodec, groupClientSecretCommitment, sealGroupClientSecret, groupSecretBoxCodec,
    openGroupSecret, agreeKey, groupClientSecretBoxAad, encryptionPublicKey } from '@meshline/sdk';

let bridge: DotnetBridge;
beforeAll(() => { bridge = new DotnetBridge(); });
afterAll(async () => { await bridge.dispose(); });
const accountKey = new Uint8Array(Array.from({ length: 32 }, (_, index) => index + 1));
const deviceKey = new Uint8Array(Array.from({ length: 32 }, (_, index) => index + 65));
const input = encodeUtf8('Meshline TypeScript / .NET interoperability 中文😀');

test('actual .NET GroupManager decrypts TypeScript content, signed extensions and nickname clearing', async () => {
    const signer = messageDevice(relayContext, 13, 14); const applicationSecret = new Uint8Array(32).fill(81);
    for (const payload of [{ $type: 'meshline.group.message.content', body: { content_type: 'text/plain', text: '群组互操作 😀' }, future: { enabled: true } },
        { $type: 'meshline.group.member.nickname.update', nickname: null, future: 'preserved' }] as JsonObject[]) {
        const envelope = await encryptGroupMessage({ context: relayContext, signer, groupId: 'grp_AAECAwQFBgcICQoLDA0ODw', epoch: 4, messageId: 'msg_AAECAwQFBgcICQoLDA0ODw', createdAt: 1730000000, applicationSecret, payload });
        const result = await bridge.invoke<{ json: string }>({ operation: 'group-open', context: relayContext.toString(), envelope: groupMessageEnvelopeCodec.stringify(envelope),
            sender: deviceCertificateCodec.stringify(signer.certificate), deviceId: signer.id, applicationSecret: encodeBase64Url(applicationSecret) });
        expect(parseJson(result.json)).toEqual(payload);
    }
});
test('.NET GroupManager AAD and key derivation produce envelopes accepted by the verified TypeScript decryption API', async () => {
    const signer = messageDevice(relayContext, 15, 16); const applicationSecret = new Uint8Array(32).fill(82);
    const payload = { $type: 'meshline.group.member.nickname.update', nickname: ' 昵称🌿 ', future: { active: true } };
    const result = await bridge.invoke<{ json: string }>({ operation: 'group-create', context: relayContext.toString(), sender: deviceCertificateCodec.stringify(signer.certificate),
        groupId: 'grp_AAECAwQFBgcICQoLDA0ODw', epoch: 7, messageId: 'msg_AAECAwQFBgcICQoLDA0ODw', createdAt: 1730000000,
        applicationSecret: encodeBase64Url(applicationSecret), privateKey: encodeBase64Url(signer.signingKey), payload: canonicalJson(payload) });
    expect(decryptGroupMessage({ context: relayContext, envelope: groupMessageEnvelopeCodec.parse(result.json), sender: signer.certificate, signerDeviceId: signer.id, applicationSecret })).toEqual(payload);
});
test('actual .NET GroupManager secret wrapping, opening and commitment interoperate in both directions', async () => {
    const signer = messageDevice(relayContext, 17, 18); const memberKey = new Uint8Array(32).fill(92); const secret = new Uint8Array(32).fill(83); const groupId = 'grp_AAECAwQFBgcICQoLDA0ODw';
    const header = { groupId, account: signer.certificate.account, memberEncryptionPublicKey: encryptionPublicKey(memberKey), clientSecretCommitment: groupClientSecretCommitment(groupId, secret, relayContext) };
    const box = sealGroupClientSecret(header, secret, relayContext);
    const result = await bridge.invoke<{ box: string; opened: string; commitment: string }>({ operation: 'group-secrets', context: relayContext.toString(), account: signer.certificate.account, groupId,
        secret: encodeBase64Url(secret), privateKey: encodeBase64Url(memberKey), box: groupSecretBoxCodec.stringify(box) });
    expect(result.commitment).toBe(header.clientSecretCommitment); expect(decodeBase64Url(result.opened)).toEqual(secret);
    const returned = groupSecretBoxCodec.parse(result.box); expect(openGroupSecret(returned, agreeKey(memberKey, returned.enc), groupClientSecretBoxAad(header, relayContext))).toEqual(secret);
});

test('channel descriptor, post, edit and deletion sign in TypeScript and round-trip through actual .NET models', async () => {
    const f = channelFixture();
    for (const payload of [f.initial.payload, f.post.payload, f.edit(2, { additionalProperties: { future: '更新😀', retained: null } }).payload, f.deletion(3).payload]) {
        const result = await bridge.invoke<{ valid: boolean; json: string; signingInput: string }>({ operation: 'channel-payload', context: f.context.toString(), json: canonicalJson(channelPayloadCodec.encode(payload)),
            publicKey: encodeBase64Url(f.owner.certificate.signingPublicKey), privateKey: encodeBase64Url(f.owner.signingKey) });
        expect(result.valid).toBe(true); expect(decodeBase64Url(result.signingInput)).toEqual(channelPayloadInput(payload, f.context));
        expect(channelPayloadCodec.encode(channelPayloadCodec.decode(parseJson(result.json)))).toEqual(channelPayloadCodec.encode(payload));
        if (payload.kind === 'descriptor') verifyChannelDescriptor({ descriptor: payload.value, signerCertificate: f.owner.certificate }, f.context, f.channel);
    }
});

test('actual .NET ChannelManager applies extension deletion and body omission identically to TypeScript', async () => {
    const f = channelFixture(); if (f.post.payload.kind !== 'post') throw new Error('Expected test post'); let post = f.post.payload.value;
    for (const event of [f.edit(2, { body: { contentType: 'text/plain', text: '双向编辑 😀' } }), f.edit(3, { additionalProperties: { retained: null, future: 'preserved' } })]) {
        if (event.payload.kind !== 'edit') throw new Error('Expected test edit');
        const result = await bridge.invoke<{ json: string; deleted: boolean }>({ operation: 'channel-apply', post: channelPostCodec.stringify(post), event: channelEventCodec.stringify(event), certificate: deviceCertificateCodec.stringify(f.owner.certificate) });
        post = applyChannelPostEdit(post, event.payload.value); expect(channelPostCodec.stringify(channelPostCodec.parse(result.json))).toBe(channelPostCodec.stringify(post)); expect(result.deleted).toBe(false);
    }
    const result = await bridge.invoke<{ json: string | null; deleted: boolean }>({ operation: 'channel-apply', post: channelPostCodec.stringify(post), event: channelEventCodec.stringify(f.deletion(4)), certificate: deviceCertificateCodec.stringify(f.owner.certificate) });
    expect(result).toEqual({ json: null, deleted: true });
});

test('new TypeScript messages decrypt with the actual .NET MessageManager implementation for recipient and sender', async () => {
    const from = messageDevice(relayContext, 1, 2); const to = messageDevice(relayContext, 3, 4);
    const payload = { $type: 'meshline.message.direct', body: { content_type: 'text/plain', text: '双向端到端😀' }, future: { value: 42 } };
    const request = await encryptMessage({ context: relayContext, signer: from, messageId: 'msg_AAECAwQFBgcICQoLDA0ODw', createdAt: 1730000000,
        recipient: to.certificate.account, recipientDevices: [to.certificate], payload });
    for (const [device, box] of [[to, request.recipientBoxes[0]!], [from, request.senderBoxes![0]!]] as const) {
        const result = await bridge.invoke<{ json: string }>({ operation: 'message-open', context: relayContext.toString(), envelope: messageEnvelopeCodec.stringify(request.envelope),
            keyBox: messageKeyBoxCodec.stringify(box), sender: deviceCertificateCodec.stringify(from.certificate), account: device.certificate.account, privateKey: encodeBase64Url(device.encryptionKey) });
        expect(parseJson(result.json)).toEqual(payload);
    }
});

test('actual .NET MessageManager key boxes and AAD decrypt through the TypeScript verified API', async () => {
    const from = messageDevice(relayContext, 5, 6); const to = messageDevice(relayContext, 7, 8);
    const payload = { $type: 'meshline.message.direct', body: { content_type: 'text/plain', text: '.NET → TypeScript 😀' }, extension: ['future', 12] };
    const result = await bridge.invoke<{ json: string }>({ operation: 'message-create', context: relayContext.toString(), sender: deviceCertificateCodec.stringify(from.certificate),
        recipient: deviceCertificateCodec.stringify(to.certificate), privateKey: encodeBase64Url(from.signingKey), messageId: 'msg_AAECAwQFBgcICQoLDA0ODw', createdAt: 1730000000,
        payload: JSON.stringify(payload) });
    const request = messageSendRequestCodec.parse(result.json);
    for (const [device, box] of [[to, request.recipientBoxes[0]!], [from, request.senderBoxes![0]!]] as const)
        expect(await decryptMessage({ context: relayContext, receiver: device, sender: from.certificate, envelope: request.envelope, keyBox: box })).toEqual(payload);
});

test('profile signature, signed extensions and historical certificate interoperate with .NET', async () => {
    const row = vector<{ device_certificate: { unsigned_object: JsonObject; device_signature: string; account_signature: string } }>('identity-auth').device_certificate;
    const certificate = deviceCertificateCodec.decode({ ...row.unsigned_object, device_signature: row.device_signature, account_signature: row.account_signature });
    let profile = accountProfileCodec.decode({ $type: 'meshline.profile', account: certificate.account, nickname: '中文😀', bio: '',
        public_discovery: false, updated_at: 1730000000, future: { nested: false }, device_signature: encodeBase64Url(new Uint8Array(64)) });
    profile = { ...profile, deviceSignature: signDevice(profileInput(profile, relayContext), deviceKey) };
    const result = await bridge.invoke<{ valid: boolean; json: string; signingInput: string }>({ operation: 'profile', context: relayContext.toString(),
        json: profileResolveResultCodec.stringify({ profile, signerCertificate: certificate }), privateKey: encodeBase64Url(deviceKey) });
    expect(result.valid).toBe(true); expect(decodeBase64Url(result.signingInput)).toEqual(profileInput(profile, relayContext));
    const roundtrip = profileResolveResultCodec.parse(result.json); validateProfileResult(roundtrip, relayContext, certificate.account);
    expect(roundtrip.profile.deviceSignature).toEqual(profile.deviceSignature);
    expect(accountProfileCodec.stringify(roundtrip.profile)).toBe(accountProfileCodec.stringify(profile));
});

test('new TypeScript relay descriptor including signed extensions validates in .NET', async () => {
    const descriptor = signedDescriptor({ additionalProperties: { future: { nested: false } } });
    const result = await bridge.invoke<{ valid: boolean; json: string }>({ operation: 'relay-descriptor', context: relayContext.toString(), json: relayDescriptorCodec.stringify(descriptor) });
    expect(result.valid).toBe(true);
    validateRelayDescriptor(relayDescriptorCodec.parse(result.json), relayContext, Math.floor(Date.now() / 1000));
    expect(relayDescriptorCodec.stringify(relayDescriptorCodec.parse(result.json))).toBe(relayDescriptorCodec.stringify(descriptor));
});

test('canonical JSON preserves extensions and Unicode identically in both implementations', async () => {
    const source = '{"z":"😀","unknown":{"values":[3,2,1],"signature":"nested"},"a":9007199254740991}';
    const expected = canonicalJson(parseJson(source));
    expect((await bridge.invoke<{ json: string }>({ operation: 'canonical', json: source })).json).toBe(expected);
    const roundtrip = await bridge.invoke<{ json: string }>({ operation: 'canonical', json: expected });
    expect(canonicalJson(parseJson(roundtrip.json))).toBe(expected);
});

test('account and relay derivation agree with actual .NET SDK', async () => {
    const key = accountPublicKey(accountKey);
    const result = await bridge.invoke<{ accountId: string; relayId: string }>({ operation: 'identity', chain: 'neo:860833102', publicKey: encodeBase64Url(key) });
    expect(result.accountId).toBe(getAccountId('neo:860833102', key));
    expect(result.relayId).toBe(getRelayId(key));
});

test('Ed25519 signatures interoperate in both directions', async () => {
    const signature = signDevice(input, deviceKey);
    const publicKey = devicePublicKey(deviceKey);
    expect(await bridge.invoke({ operation: 'device-verify', input: encodeBase64Url(input), signature: encodeBase64Url(signature), publicKey: encodeBase64Url(publicKey) })).toEqual({ valid: true });
    const result = await bridge.invoke<{ signature: string }>({ operation: 'device-sign', input: encodeBase64Url(input), privateKey: encodeBase64Url(deviceKey) });
    expect(decodeBase64Url(result.signature)).toEqual(signature);
    expect(verifyDevice(input, decodeBase64Url(result.signature), publicKey)).toBe(true);
});

test('P-256 signatures interoperate without requiring identical randomized signature bytes', async () => {
    const signature = signAccount(input, accountKey);
    expect(await bridge.invoke({ operation: 'account-verify', input: encodeBase64Url(input), signature: encodeBase64Url(signature), publicKey: encodeBase64Url(accountPublicKey(accountKey)) })).toEqual({ valid: true });
    const result = await bridge.invoke<{ signature: string; publicKey: string }>({ operation: 'account-sign', input: encodeBase64Url(input), privateKey: encodeBase64Url(accountKey) });
    expect(verifyAccount(input, decodeBase64Url(result.signature), decodeBase64Url(result.publicKey))).toBe(true);
});

test('AES-GCM ciphertext decrypts in both implementations and rejects modified AAD', async () => {
    const nonce = new Uint8Array(12).fill(7);
    const aad = encodeUtf8('independent provider test');
    const ciphertext = encryptAes(accountKey, nonce, input, aad);
    const common = { key: encodeBase64Url(accountKey), nonce: encodeBase64Url(nonce), aad: encodeBase64Url(aad) };
    expect(await bridge.invoke({ ...common, operation: 'decrypt', ciphertext: encodeBase64Url(ciphertext) })).toEqual({ plaintext: encodeBase64Url(input) });
    const result = await bridge.invoke<{ ciphertext: string }>({ ...common, operation: 'encrypt', plaintext: encodeBase64Url(input) });
    expect(decodeBase64Url(result.ciphertext)).toEqual(ciphertext);
    expect(decryptAes(accountKey, nonce, decodeBase64Url(result.ciphertext), aad)).toEqual(input);
    await expect(bridge.invoke({ ...common, operation: 'decrypt', ciphertext: encodeBase64Url(ciphertext), aad: '' })).rejects.toThrow();
});

test('new TypeScript certificate with signed extensions validates in .NET and roundtrips', async () => {
    const row = vector<{ network_context: string; device_certificate: { unsigned_object: JsonObject; device_signature: string; account_signature: string } }>('identity-auth');
    const context = NetworkContext.parse(row.network_context);
    let certificate = deviceCertificateCodec.decode({ ...row.device_certificate.unsigned_object, device_signature: row.device_certificate.device_signature, account_signature: row.device_certificate.account_signature, extension: { active: false, nested: ['preserve'] } });
    certificate = { ...certificate, deviceSignature: signDevice(certificateDeviceInput(certificate, context), deviceKey) };
    certificate = { ...certificate, accountSignature: signAccount(certificateAccountInput(certificate, context), accountKey) };
    const result = await bridge.invoke<{ valid: boolean; deviceId: string; json: string }>({ operation: 'certificate', context: context.toString(), json: deviceCertificateCodec.stringify(certificate) });
    expect(result.valid).toBe(true);
    expect(result.deviceId).toBe(certificateId(certificate, context));
    validateCertificate(deviceCertificateCodec.parse(result.json), context);
    expect(result.json).toBe(deviceCertificateCodec.stringify(certificate));
});
