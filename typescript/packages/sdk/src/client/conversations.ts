import { ProtocolError } from '../errors.js';
import { decodeGroupProjection } from '../groups/repository.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import { snapshotReader } from '../messages/repository.js';
import { channelPostCodec, validateChannelRef } from '../models/channels.js';
import { groupEventCodec } from '../models/group-management.js';
import { groupMessageCodec, groupMessageEnvelopeCodec } from '../models/groups.js';
import { directMessageCodec } from '../models/messages.js';
import { requireObject, requireSafeInteger } from '../protocol/json.js';
import type { MeshlineStore, QueryReader, RecordQuery, StoreSnapshot } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';

export type ConversationKind = 'direct' | 'group' | 'channel';
export interface ConversationSummary { readonly sender: string; readonly timestamp: number; readonly text?: string; readonly hasAttachments: boolean }
export interface Conversation { readonly conversationId: string; readonly kind: ConversationKind; readonly latest?: ConversationSummary; readonly unreadCount: number }
export interface ConversationQuery { readonly kinds?: readonly ConversationKind[]; readonly unreadOnly?: boolean; readonly hasMessages?: boolean }
export interface ConversationChange { readonly conversationId: string; readonly kind: 'created' | 'updated' | 'removed' }
interface Content { sequence: number; summary: ConversationSummary }
interface Projection { conversation: Conversation; head?: number }
const queries: readonly RecordQuery[] = ['messages', 'groups', 'group_events', 'channels', 'channel_posts', 'conversation_reads'].map(collection => ({ collection }));
const readKey = (conversationId: string) => ({ collection: 'conversation_reads', key: conversationId });
const compare = (a: string, b: string) => a === b ? 0 : a < b ? -1 : 1;
export function conversationKind(id: string): ConversationKind {
    if (id.startsWith('grp_')) { validateIdentifier('group', id); return 'group'; }
    if (id.startsWith('chan_')) { validateIdentifier('channel', id); return 'channel'; }
    validateAccountId(id); return 'direct';
}
function content(sequence: unknown, sender: unknown, timestamp: unknown, value: { readonly body?: { readonly text?: string }; readonly attachments?: readonly unknown[] }): Content {
    requireSafeInteger(sequence, 0); requireSafeInteger(timestamp, 0); if (typeof sender !== 'string') throw new ProtocolError('invalid_storage', 'Conversation item has no sender.'); validateAccountId(sender);
    return { sequence, summary: { sender, timestamp, ...(value.body?.text === undefined ? {} : { text: value.body.text }), hasAttachments: Boolean(value.attachments?.length) } };
}

/** All summaries and read positions come from one fixed storage snapshot. */
export class ClientConversations {
    constructor(readonly store: MeshlineStore, readonly accountId: string) {}
    #project(snapshot: StoreSnapshot, includeUnfollowed = false): readonly Projection[] {
        const [messages, groups, groupEvents, channels, channelPosts, reads] = snapshot.sets; const positions = new Map<string, number>();
        for (const row of reads!) { conversationKind(row.key); requireSafeInteger(row.value.sequence, 0); positions.set(row.key, row.value.sequence); }
        const records = new Map<string, { kind: ConversationKind; items: Content[] }>();
        const add = (id: string, kind: ConversationKind, item?: Content) => { const record = records.get(id) ?? { kind, items: [] }; if (item) record.items.push(item); records.set(id, record); };
        for (const row of messages!) {
            const value = row.value; if (value.payloadType !== 'meshline.message.direct' || value.sender !== this.accountId && value.recipient !== this.accountId) continue;
            if (typeof value.sender !== 'string' || typeof value.recipient !== 'string') throw new ProtocolError('invalid_storage', 'Direct conversation has malformed participants.');
            const peer = value.sender === this.accountId ? value.recipient : value.sender; validateAccountId(peer);
            add(peer, 'direct', content(value.localSequence, value.sender, value.createdAt, directMessageCodec.decode(value.payload!)));
        }
        const knownGroups = new Set(groups!.map(row => row.key));
        for (const row of groups!) {
            const group = { groupId: row.key, relayId: String(row.value.relayId) }; if (!row.value.projection) continue;
            const state = decodeGroupProjection(requireObject(row.value.projection), group);
            if (row.value.localDepartureAfter !== undefined) { requireSafeInteger(row.value.localDepartureAfter, 0); if (state.sequence <= row.value.localDepartureAfter) continue; }
            if (state.members.some(value => value.account === this.accountId)) add(row.key, 'group');
        }
        for (const row of groupEvents!) {
            const value = row.value; if (value.isMessage !== true || value.decryptedPayload === undefined) continue;
            const event = groupEventCodec.decode(value.event!); const envelope = groupMessageEnvelopeCodec.decode(event.payload);
            if (!knownGroups.has(envelope.groupId) || row.key !== `${envelope.groupId}|${String(event.sequence).padStart(16, '0')}`) throw new ProtocolError('invalid_storage', 'Group conversation event has no consistent hosting association.');
            add(envelope.groupId, 'group', content(event.sequence, requireObject(value.message!).sender, envelope.createdAt, groupMessageCodec.decode(value.decryptedPayload)));
        }
        const followed = new Set<string>();
        for (const row of channels!) {
            validateChannelRef({ channelId: row.key, relayId: String(row.value.relayId) });
            if (typeof row.value.isFollowed !== 'boolean') throw new ProtocolError('invalid_storage', 'Channel conversation has no followed state.');
            if (row.value.isFollowed || includeUnfollowed) { followed.add(row.key); add(row.key, 'channel'); }
        }
        for (const row of channelPosts!) {
            const value = row.value; const channelId = row.key.split('|')[0]!; if (!followed.has(channelId) || value.isDeleted === true || value.post === undefined) continue;
            const post = channelPostCodec.decode(value.post); if (post.channelId !== channelId || row.key !== `${channelId}|${String(value.sequence).padStart(16, '0')}`) throw new ProtocolError('invalid_storage', 'Channel conversation post differs from its storage identity.');
            add(channelId, 'channel', content(value.sequence, value.author, value.acceptedAt, post));
        }
        return [...records].map(([conversationId, record]) => {
            const position = positions.get(conversationId) ?? -1; let latest: Content | undefined; let unreadCount = 0;
            for (const item of record.items) { if (!latest || item.sequence > latest.sequence) latest = item; if (item.summary.sender !== this.accountId && item.sequence > position) unreadCount++; }
            return { conversation: { conversationId, kind: record.kind, unreadCount, ...(latest ? { latest: latest.summary } : {}) }, ...(latest ? { head: latest.sequence } : {}) };
        }).sort((a, b) => (b.conversation.latest?.timestamp ?? -1) - (a.conversation.latest?.timestamp ?? -1) || compare(a.conversation.conversationId, b.conversation.conversationId));
    }
    async list(query: ConversationQuery = {}, signal?: AbortSignal): Promise<QueryReader<Conversation>> {
        const kinds = query.kinds && [...query.kinds]; const { unreadOnly, hasMessages } = query;
        if (kinds?.some(value => !['direct', 'group', 'channel'].includes(value)) || unreadOnly !== undefined && typeof unreadOnly !== 'boolean' || hasMessages !== undefined && typeof hasMessages !== 'boolean') throw new TypeError('Invalid conversation filter.');
        const records = this.#project(await this.store.read(queries, signal));
        return snapshotReader(records.map(value => value.conversation).filter(value => (!kinds || kinds.includes(value.kind)) && (!unreadOnly || value.unreadCount > 0) && (hasMessages === undefined || Boolean(value.latest) === hasMessages)));
    }
    async get(id: string, signal?: AbortSignal): Promise<Conversation | undefined> { conversationKind(id); return this.#project(await this.store.read(queries, signal)).find(value => value.conversation.conversationId === id)?.conversation; }
    async ids(signal?: AbortSignal): Promise<ReadonlySet<string>> { return new Set(this.#project(await this.store.read(queries, signal)).map(value => value.conversation.conversationId)); }
    async markRead(id: string, signal?: AbortSignal): Promise<boolean> {
        conversationKind(id);
        return updateStore(this.store, queries, snapshot => {
            const projection = this.#project(snapshot, true).find(value => value.conversation.conversationId === id); const sequence = projection?.head;
            const before = snapshot.sets[5]!.find(row => row.key === id)?.value.sequence ?? -1; requireSafeInteger(before, -1);
            if (sequence === undefined || sequence <= before) return { mutations: [], result: false };
            return { mutations: [{ kind: 'put', ...readKey(id), value: { sequence } }], result: true };
        }, signal);
    }
}
