import { expect, test } from 'vitest';
import {
    NetworkContext, applyChannelPostEdit, channelPostCodec, channelPostEditCodec, channelPayloadCodec, channelPayloadInput, validateChannelPayload,
    channelDescriptorCodec, channelDescriptorInput, validateChannelDescriptor, verifyChannelDescriptor, verifyChannelEvent, validateChannelReadPage,
    validateChannelReadQuery, validateChannelSubscriptionRequest, signingInput, signDevice, decodeBase64Url, encodeBase64Url,
    deriveResourceId, certificateId, type ChannelDescriptor, type ChannelEvent, type ChannelReadPage, type JsonObject,
} from '@meshline/sdk';
import { hex, vector } from '../support/vectors.js';
import { messageDevice } from '../support/message-fixture.js';
import { signedDescriptor } from '../support/relay-fixture.js';

const data = vector<{ network_context: string; channel_signer: { private_key: string }; channel_write_cases: { name: string; request: JsonObject; signing_input_utf8_hex: string }[];
    channel_edit_sequences: { name: string; post_case: string; steps: { request: JsonObject; signing_input_utf8_hex: string; expected: { status: string; body?: JsonObject; attachments?: JsonObject[] } }[] }[] }>('channels');
const context = NetworkContext.parse(data.network_context); const key = decodeBase64Url(data.channel_signer.private_key);
const writes = new Map(data.channel_write_cases.map(row => [row.name, row]));
test.each(data.channel_write_cases)('channel independent signature and content vector: $name', row => {
    const payload = channelPayloadCodec.decode(row.request); validateChannelPayload(payload, context);
    const input = channelPayloadInput(payload, context); expect(hex(input)).toBe(row.signing_input_utf8_hex);
    expect(encodeBase64Url(signDevice(input, key))).toBe(row.request.device_signature); expect(channelPayloadCodec.encode(payload)).toEqual(row.request);
});
test.each(data.channel_edit_sequences)('channel edit acceptance sequence: $name', sequence => {
    let post = channelPostCodec.decode(writes.get(sequence.post_case)!.request);
    for (const step of sequence.steps) {
        const input = signingInput(step.request, context, ['device_signature']); expect(hex(input)).toBe(step.signing_input_utf8_hex);
        expect(encodeBase64Url(signDevice(input, key))).toBe(step.request.device_signature);
        const apply = () => applyChannelPostEdit(post, channelPostEditCodec.decode(step.request));
        if (step.expected.status === 'bad_request') { expect(apply).toThrow(); continue; }
        post = apply(); const wire = channelPostCodec.encode(post);
        expect(wire.body).toEqual(step.expected.body); expect(wire.attachments).toEqual(step.expected.attachments);
    }
});

function fixture() {
    const device = messageDevice(context, 2, 4); const relayId = signedDescriptor().relayId; const nonce = new Uint8Array(16).fill(9);
    let descriptor: ChannelDescriptor = { channelId: deriveResourceId('channel', device.certificate.account, relayId, nonce, context), nonce, creator: device.certificate.account, relayId,
        name: '频道 😀', description: '', moderators: [], revision: 0, status: 'active', createdAt: 1730000000, updatedAt: 1730000000, deviceSignature: new Uint8Array(64) };
    descriptor = { ...descriptor, deviceSignature: signDevice(channelDescriptorInput(descriptor, context), device.signingKey) };
    const channel = { channelId: descriptor.channelId, relayId };
    return { device, descriptor, channel };
}
test('descriptor binds creator/relay/nonce/context and verifies historical signer evidence', () => {
    const { device, descriptor, channel } = fixture(); verifyChannelDescriptor({ descriptor, signerCertificate: device.certificate }, context, channel);
    expect(() => validateChannelDescriptor({ ...descriptor, name: '  ' }, context)).toThrow();
    expect(() => validateChannelDescriptor({ ...descriptor, moderators: [descriptor.creator] }, context)).toThrow();
    expect(() => validateChannelDescriptor({ ...descriptor, revision: 0, status: 'closed' }, context)).toThrow();
    expect(() => validateChannelDescriptor({ ...descriptor, nonce: new Uint8Array(16) }, context)).toThrow();
    expect(() => verifyChannelDescriptor({ descriptor: { ...descriptor, name: 'tampered' }, signerCertificate: device.certificate }, context, channel)).toThrow('signature');
    expect(() => validateChannelDescriptor({ ...descriptor, additionalProperties: { extension: null } }, context)).toThrow('Null');
});
test('channel edits preserve omission and root extension deletion while rejecting nested null and immutable fields', () => {
    const post = { ...channelPostCodec.decode(writes.get('post_markdown_and_image')!.request), additionalProperties: { custom: { revision: 1 }, retained: true } };
    const edit = channelPostEditCodec.decode({ $type: 'meshline.channel.post.edit', channel_id: post.channelId, target_sequence: 1, device_signature: encodeBase64Url(new Uint8Array(64)), custom: null });
    const result = applyChannelPostEdit(post, edit); expect(result.additionalProperties).toEqual({ retained: true }); expect(result.body).toEqual(post.body); expect(post.additionalProperties.custom).toEqual({ revision: 1 });
    expect(() => applyChannelPostEdit(post, { ...edit, additionalProperties: { custom: { nested: null } } })).toThrow('Null');
    expect(() => applyChannelPostEdit(post, { ...edit, additionalProperties: { message_id: 'replacement' } })).toThrow();
});
test('timeline validates ordering, bounds, certificate coverage, revision and historical moderator authority', () => {
    const { device, descriptor, channel } = fixture(); const stranger = messageDevice(context, 3, 6);
    const source = channelPostCodec.decode(writes.get('post_image_only')!.request);
    const payload = { kind: 'post' as const, value: { ...source, channelId: channel.channelId } }; payload.value.deviceSignature = signDevice(channelPayloadInput(payload, context), device.signingKey);
    const initial: ChannelEvent = { sequence: 0, descriptorRev: 0, payload: { kind: 'descriptor', value: descriptor }, acceptedAt: 1730000000, signerDeviceId: certificateId(device.certificate, context) };
    const event: ChannelEvent = { ...initial, sequence: 1, payload }; const page: ChannelReadPage = { events: [initial, event], certificates: [device.certificate], hasMore: false };
    validateChannelReadPage(page, { channelId: channel.channelId, after: -1 }, context); verifyChannelEvent(event, device.certificate, descriptor, context, channel);
    expect(() => validateChannelReadPage({ ...page, events: [event, initial] }, { channelId: channel.channelId }, context)).toThrow('order');
    expect(() => validateChannelReadPage(page, { channelId: channel.channelId, before: 1 }, context)).toThrow('bounds');
    expect(() => validateChannelReadPage({ ...page, certificates: [] }, { channelId: channel.channelId }, context)).toThrow('signing evidence');
    expect(() => validateChannelReadPage({ ...page, events: [], hasMore: true }, { channelId: channel.channelId }, context)).toThrow('pagination');
    expect(() => verifyChannelEvent({ ...event, signerDeviceId: certificateId(stranger.certificate, context) }, stranger.certificate, descriptor, context, channel)).toThrow('authorized');
    expect(() => verifyChannelEvent(event, device.certificate, { ...descriptor, revision: 1, status: 'closed' }, context, channel)).toThrow('authorized');
    expect(() => validateChannelReadQuery({ channelId: channel.channelId, before: 1, after: 0 })).toThrow('combined');
    expect(() => validateChannelSubscriptionRequest({ channelIds: [channel.channelId, channel.channelId] })).toThrow('duplicate');
    expect(channelDescriptorCodec.decode(channelDescriptorCodec.encode(descriptor))).toEqual(descriptor);
});
