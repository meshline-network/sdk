import {
    certificateId, deriveResourceId, channelPayloadInput, signDevice, type ChannelDescriptor, type ChannelEvent, type ChannelPayload,
    type ChannelReadPage, type ChannelReadQuery, type ChannelResolveResult,
} from '@meshline/sdk';
import { messageDevice } from './message-fixture.js';
import { context, signedDescriptor } from './relay-fixture.js';
export function channelFixture() {
    const owner = messageDevice(context, 2, 4); const moderator = messageDevice(context, 3, 5); const relayId = signedDescriptor().relayId; const nonce = new Uint8Array(16).fill(7);
    const channel = { channelId: deriveResourceId('channel', owner.certificate.account, relayId, nonce, context), relayId };
    const sign = <T extends ChannelPayload>(payload: T, device = owner): T => ({ ...payload, value: { ...payload.value, deviceSignature: signDevice(channelPayloadInput(payload, context), device.signingKey) } });
    const descriptor = sign({ kind: 'descriptor', value: { ...channel, nonce, creator: owner.certificate.account, name: '频道', revision: 0,
        moderators: [moderator.certificate.account], status: 'active', createdAt: 1730000000, updatedAt: 1730000000, deviceSignature: new Uint8Array(64) } satisfies ChannelDescriptor }).value;
    const descriptors = new Map<number, ChannelResolveResult>([[0, { descriptor, signerCertificate: owner.certificate }]]);
    const event = (sequence: number, payload: ChannelPayload, revision = 0, device = owner): ChannelEvent => ({ sequence, descriptorRev: revision, payload: sign(payload, device), acceptedAt: 1730000000 + sequence, signerDeviceId: certificateId(device.certificate, context) });
    const initial = event(0, { kind: 'descriptor', value: descriptor });
    const post = event(1, { kind: 'post', value: { channelId: channel.channelId, messageId: 'msg_AAECAwQFBgcICQoLDA0ODw', body: { contentType: 'text/plain', text: 'original' }, additionalProperties: { retained: 'original' }, deviceSignature: new Uint8Array(64) } });
    const edit = (sequence: number, fields: { body?: { contentType: string; text: string } | null; additionalProperties?: Record<string, string | null> }) => event(sequence, { kind: 'edit', value: { channelId: channel.channelId, targetSequence: 1, ...fields, deviceSignature: new Uint8Array(64) } });
    const deletion = (sequence: number) => event(sequence, { kind: 'delete', value: { channelId: channel.channelId, targetSequence: 1, deviceSignature: new Uint8Array(64) } });
    const pages = (events: readonly ChannelEvent[], hasMore = false): ChannelReadPage => ({ events, certificates: [owner.certificate, moderator.certificate], hasMore });
    const reads: ChannelReadQuery[] = [];
    let timeline = [initial, post]; let beforeRead: (() => Promise<void>) | undefined;
    const reader = {
        async read(query: ChannelReadQuery): Promise<ChannelReadPage> {
            reads.push(query); await beforeRead?.(); const limit = query.limit ?? 100;
            const selected = timeline.filter(entry => (query.before === undefined || entry.sequence < query.before) && (query.after === undefined || entry.sequence > query.after));
            return pages(query.after === undefined ? selected.slice(-limit) : selected.slice(0, limit), selected.length > limit);
        },
        async resolve(revision: number): Promise<ChannelResolveResult> { const result = descriptors.get(revision); if (!result) throw new Error('Missing fixture descriptor'); return result; },
    };
    return { context, owner, moderator, channel, descriptor, descriptors, event, post, initial, edit, deletion, sign, pages, reads, reader,
        set timeline(value: ChannelEvent[]) { timeline = value; }, set beforeRead(value: (() => Promise<void>) | undefined) { beforeRead = value; } };
}
