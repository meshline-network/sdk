import * as sdk from '@meshline/sdk';
import { MessagingNetwork } from './messaging-network.js';
import { context } from './relay-fixture.js';
export class ChannelNetwork {
    readonly channels = new Map<string, { descriptors: Map<number, sdk.ChannelResolveResult>; events: sdk.ChannelEvent[]; certificates: Map<string, sdk.DeviceCertificate>; posts: Map<number, sdk.ChannelPost> }>();
    readonly requests: { method: string; body: sdk.JsonObject }[] = []; readonly reports: sdk.JsonObject[] = [];
    loseResponse: string | undefined; failRead = false; wrongSequence = false;
    constructor(readonly network = new MessagingNetwork()) {
        const previous = network.handleRequest;
        network.handleRequest = async (method, body, account, id) => await this.#handle(method, body, account, id) ?? previous?.(method, body, account, id);
    }
    async #handle(method: string, body: sdk.JsonObject, account: string, deviceId: string): Promise<Response | undefined> {
        if (!method.startsWith('channel.')) return undefined;
        const response = (value?: unknown, status = value === undefined ? 204 : 200): Response => new Response(value === undefined ? null : JSON.stringify(value), { status, ...(value === undefined ? {} : { headers: { 'content-type': 'application/json' } }) });
        const error = (code: string, message = code, status = 409): Response => response({ code, message }, status);
        this.requests.push({ method, body }); const id = String(body.channel_id); let channel = this.channels.get(id);
        if (method === 'channel.resolve') {
            const result = body.revision === undefined ? [...channel?.descriptors.values() ?? []].at(-1) : channel?.descriptors.get(Number(body.revision));
            return result ? response(sdk.channelResolveResultCodec.encode(result)) : error('not_found', 'Missing descriptor', 404);
        }
        if (method === 'channel.read') {
            if (this.failRead) throw new Error('History unavailable'); if (!channel) return error('not_found', 'Missing channel', 404);
            const after = body.after === undefined ? undefined : Number(body.after); const before = body.before === undefined ? undefined : Number(body.before); const limit = Number(body.limit ?? 100);
            const all = channel.events.filter(entry => (after === undefined || entry.sequence > after) && (before === undefined || entry.sequence < before)); const events = after === undefined ? all.slice(-limit) : all.slice(0, limit);
            const certificates = [...new Set(events.map(entry => entry.signerDeviceId))].map(id => channel!.certificates.get(id)!);
            return response(sdk.channelReadPageCodec.encode({ events, certificates, hasMore: all.length > limit }));
        }
        if (method === 'channel.post.report') { sdk.validateChannelReportRequest(sdk.channelReportRequestCodec.decode(body)); this.reports.push(body); return response(); }
        const certificate = sdk.authorizedDevice(this.network.states.get(account)!, deviceId, context, this.network.clock.wall);
        let payload: sdk.ChannelPayload;
        if (method === 'channel.close') { if (!channel) return error('not_found'); const current = [...channel.descriptors.values()].at(-1)!.descriptor; const request = sdk.channelCloseRequestCodec.decode(body);
            payload = { kind: 'descriptor', value: { ...current, status: 'closed', revision: request.revision, updatedAt: request.updatedAt, deviceSignature: request.deviceSignature } };
        } else payload = sdk.channelPayloadCodec.decode(body);
        sdk.validateChannelPayload(payload, context);
        const reference = { channelId: id, relayId: this.network.descriptor.relayId };
        if (payload.kind === 'descriptor') {
            const descriptor = payload.value; sdk.verifyChannelDescriptor({ descriptor, signerCertificate: certificate }, context, reference);
            if (!channel) { if (method !== 'channel.create' || descriptor.revision !== 0) return error('not_found'); channel = { descriptors: new Map(), events: [], certificates: new Map(), posts: new Map() }; this.channels.set(id, channel); }
            const previous = channel.descriptors.get(descriptor.revision);
            if (previous) return sdk.channelDescriptorCodec.stringify(previous.descriptor) === sdk.channelDescriptorCodec.stringify(descriptor) ? response() : error('state_conflict');
            const last = [...channel.descriptors.values()].at(-1)?.descriptor;
            if (last && (last.status === 'closed' || descriptor.revision !== last.revision + 1 || account !== last.creator)) return error('state_conflict');
            channel.descriptors.set(descriptor.revision, { descriptor, signerCertificate: certificate });
        } else {
            if (!channel) return error('not_found'); const current = [...channel.descriptors.values()].at(-1)!.descriptor;
            if (payload.kind === 'post') {
                const incoming = payload.value; const previous = channel.events.find(event => event.payload.kind === 'post' && event.payload.value.messageId === incoming.messageId && channel!.certificates.get(event.signerDeviceId)!.account === account);
                if (previous) return sdk.canonicalJson(sdk.channelPayloadCodec.encode(previous.payload)) === sdk.canonicalJson(sdk.channelPayloadCodec.encode(payload)) ? response({ sequence: previous.sequence }) : error('state_conflict');
            }
            if (current.status === 'closed') return error('state_conflict');
            sdk.verifyChannelEvent({ sequence: channel.events.length, descriptorRev: current.revision, payload, acceptedAt: this.network.clock.wall, signerDeviceId: deviceId }, certificate, current, context, reference);
            if (payload.kind === 'edit') { const original = channel.posts.get(payload.value.targetSequence); if (!original) return error('not_found'); const updated = sdk.applyChannelPostEdit(original, payload.value);
                if (sdk.channelPostCodec.stringify(original) === sdk.channelPostCodec.stringify(updated)) return response(); channel.posts.set(payload.value.targetSequence, updated);
            } else if (payload.kind === 'delete') { if (!channel.posts.has(payload.value.targetSequence)) return response(); channel.posts.delete(payload.value.targetSequence); }
        }
        const revision = [...channel!.descriptors.keys()].at(-1)!; const sequence = channel!.events.length;
        channel!.events.push({ sequence, descriptorRev: revision, payload, acceptedAt: this.network.clock.wall, signerDeviceId: deviceId }); channel!.certificates.set(deviceId, certificate);
        if (payload.kind === 'post') channel!.posts.set(sequence, payload.value);
        if (this.loseResponse === method) throw new Error('Channel operation accepted but response lost');
        return payload.kind === 'post' ? response({ sequence: this.wrongSequence ? sequence + 100 : sequence }) : response();
    }
    async client(seed: number, path?: string) {
        const base = await this.network.client(seed, path); const channels = new sdk.ChannelManager({ context, accountId: base.accountId, store: base.store, relayClients: base.pool, deviceManager: base.device, clock: this.network.clock });
        this.network.resources.push(channels); await channels.initialize(); return { ...base, channels, async dispose() { await channels.dispose(); await base.dispose(); } };
    }
    dispose(): Promise<void> { return this.network.dispose(); }
}
