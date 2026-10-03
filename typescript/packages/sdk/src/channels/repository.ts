import { ProtocolError, StateConflictError } from '../errors.js';
import {
    applyChannelPostEdit, channelDescriptorCodec, channelEventCodec, channelPayloadCodec, channelPostCodec, channelReadPageCodec, channelResolveResultCodec,
    validateChannelPayload, validateChannelReadPage, validateChannelReadQuery, validateChannelRef, validateChannelPostRef, verifyChannelDescriptor, verifyChannelEvent,
    type ChannelDescriptor, type ChannelEvent, type ChannelPayload, type ChannelPost, type ChannelPostRef, type ChannelReadPage, type ChannelReadQuery, type ChannelRef, type ChannelResolveResult,
} from '../models/channels.js';
import type { DeviceCertificate } from '../models/identity.js';
import type { ContentReference, MessageBody } from '../models/content.js';
import type { NetworkContext } from '../protocol/context.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import type { MeshlineStore, QueryReader, RecordKey, RecordQuery, StoredRecord, StoreMutation, StoreSnapshot } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { snapshotReader } from '../messages/repository.js';

export interface ChannelInfo { readonly ref: ChannelRef; readonly descriptor?: ChannelDescriptor; readonly isFollowed: boolean }
export interface ChannelPostInfo { readonly ref: ChannelPostRef; readonly messageId: string; readonly author: string; readonly acceptedAt: number; readonly body?: MessageBody; readonly attachments?: readonly ContentReference[] }
export interface ChannelPostChange { readonly ref: ChannelPostRef; readonly kind: 'added' | 'edited' | 'deleted'; readonly info?: ChannelPostInfo }
export interface ChannelChanges { readonly channel?: ChannelInfo; readonly posts: readonly ChannelPostChange[] }
export interface ChannelRecord { readonly channel: ChannelRef; readonly descriptor?: ChannelDescriptor; readonly syncSequence: number; readonly isFollowed: boolean }
interface PostRecord { sequence: number; appliedThrough: number; isDeleted: boolean; messageId?: string; author?: string; acceptedAt?: number; original?: ChannelPost; post?: ChannelPost }
export type ChannelOperationMethod = 'channel.create' | 'channel.update' | 'channel.close' | 'channel.post' | 'channel.post.edit' | 'channel.post.delete';
export interface ChannelOperation { readonly channel: ChannelRef; readonly method: ChannelOperationMethod; readonly payload: ChannelPayload; readonly acceptedSequence?: number }
export interface ChannelReader { read(query: ChannelReadQuery, signal?: AbortSignal): Promise<ChannelReadPage>; resolve(revision: number, signal?: AbortSignal): Promise<ChannelResolveResult> }
const prefix = (channel: ChannelRef): string => `${channel.channelId}|`;
const index = (number: number): string => { requireSafeInteger(number, 0); return String(number).padStart(16, '0'); };
const channelKey = (channel: ChannelRef): RecordKey => ({ collection: 'channels', key: channel.channelId });
const postKey = (channel: ChannelRef, sequence: number): RecordKey => ({ collection: 'channel_posts', key: `${prefix(channel)}${index(sequence)}` });
const descriptorKey = (channel: ChannelRef, revision: number): RecordKey => ({ collection: 'channel_descriptors', key: `${prefix(channel)}${index(revision)}` });
const eventKey = (channel: ChannelRef, sequence: number): RecordKey => ({ collection: 'channel_events', key: `${prefix(channel)}${index(sequence)}` });
const put = (key: RecordKey, value: JsonObject): StoreMutation => ({ ...key, kind: 'put', value });
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
const sameRows = (a: readonly StoredRecord[], b: readonly StoredRecord[]): boolean => a.length === b.length && a.every((row, i) => row.key === b[i]!.key && fingerprint(row.value) === fingerprint(b[i]!.value));
export function channelOperationKey(operation: ChannelOperation): RecordKey {
    const target = operation.payload.kind === 'edit' || operation.payload.kind === 'delete' ? `post:${index(operation.payload.value.targetSequence)}` : 'channel';
    return { collection: 'channel_operations', key: `${prefix(operation.channel)}${target}` };
}
export function encodeChannelOperation(operation: ChannelOperation): JsonObject { return { channelId: operation.channel.channelId, relayId: operation.channel.relayId, method: operation.method, payload: channelPayloadCodec.encode(operation.payload), ...(operation.acceptedSequence === undefined ? {} : { acceptedSequence: operation.acceptedSequence }) }; }
export function readChannelOperation(row: StoredRecord): ChannelOperation {
    const value = row.value; const channel = { channelId: String(value.channelId), relayId: String(value.relayId) }; validateChannelRef(channel);
    const payload = channelPayloadCodec.decode(value.payload!); const method = value.method as ChannelOperationMethod;
    const expected = payload.kind === 'descriptor' ? ['channel.create', 'channel.update', 'channel.close'] : payload.kind === 'post' ? ['channel.post'] : [`channel.post.${payload.kind}`];
    if (value.acceptedSequence !== undefined) { requireSafeInteger(value.acceptedSequence, 1); if (method !== 'channel.post') throw new ProtocolError('invalid_storage', 'Only a publication has an accepted sequence.'); }
    const result = { channel, method, payload, ...(value.acceptedSequence === undefined ? {} : { acceptedSequence: value.acceptedSequence }) };
    if (!expected.includes(method) || payload.value.channelId !== channel.channelId || channelOperationKey(result).key !== row.key) throw new ProtocolError('invalid_storage', 'Stored channel operation has inconsistent method or resource binding.');
    return result;
}
function decodeChannel(value: JsonObject | undefined, channel: ChannelRef): ChannelRecord {
    if (!value) return { channel, syncSequence: -1, isFollowed: false };
    if (value.relayId !== channel.relayId || typeof value.isFollowed !== 'boolean') throw new ProtocolError('invalid_binding', 'Stored channel belongs to another relay or is malformed.');
    requireSafeInteger(value.syncSequence, -1);
    return { channel, syncSequence: value.syncSequence, isFollowed: value.isFollowed, ...(value.descriptor ? { descriptor: channelDescriptorCodec.decode(value.descriptor) } : {}) };
}
function encodeChannel(value: ChannelRecord): JsonObject { return { relayId: value.channel.relayId, syncSequence: value.syncSequence, isFollowed: value.isFollowed, ...(value.descriptor ? { descriptor: channelDescriptorCodec.encode(value.descriptor) } : {}) }; }
function channelInfo(value: ChannelRecord): ChannelInfo { return { ref: value.channel, isFollowed: value.isFollowed, ...(value.descriptor ? { descriptor: value.descriptor } : {}) }; }
function decodePost(value: JsonObject): PostRecord {
    requireSafeInteger(value.sequence, 1); requireSafeInteger(value.appliedThrough, 0);
    if (typeof value.isDeleted !== 'boolean') throw new ProtocolError('invalid_storage', 'Malformed channel post deletion marker.');
    if (value.acceptedAt !== undefined) requireSafeInteger(value.acceptedAt, 0);
    return { sequence: value.sequence, appliedThrough: value.appliedThrough, isDeleted: value.isDeleted,
        ...(value.messageId === undefined ? {} : { messageId: String(value.messageId), author: String(value.author), acceptedAt: value.acceptedAt as number }),
        ...(value.original ? { original: channelPostCodec.decode(value.original) } : {}), ...(value.post ? { post: channelPostCodec.decode(value.post) } : {}) };
}
function encodePost(value: PostRecord): JsonObject { return { sequence: value.sequence, appliedThrough: value.appliedThrough, isDeleted: value.isDeleted,
    ...(value.messageId === undefined ? {} : { messageId: value.messageId, author: value.author!, acceptedAt: value.acceptedAt! }),
    ...(value.original ? { original: channelPostCodec.encode(value.original) } : {}), ...(value.post ? { post: channelPostCodec.encode(value.post) } : {}) }; }
function postInfo(record: PostRecord, channel: ChannelRef): ChannelPostInfo | undefined {
    if (record.isDeleted || !record.post) return undefined;
    if (!record.messageId || !record.author || record.acceptedAt === undefined) throw new ProtocolError('invalid_storage', 'Visible post lacks its original publication metadata.');
    return { ref: { channel, sequence: record.sequence }, messageId: record.messageId, author: record.author, acceptedAt: record.acceptedAt,
        ...(record.post.body ? { body: record.post.body } : {}), ...(record.post.attachments ? { attachments: record.post.attachments } : {}) };
}
function target(event: ChannelEvent): number | undefined { return event.payload.kind === 'post' ? event.sequence : event.payload.kind === 'edit' || event.payload.kind === 'delete' ? event.payload.value.targetSequence : undefined; }
function storeDescriptor(record: ChannelRecord, result: ChannelResolveResult, known: Map<number, ChannelResolveResult>): ChannelRecord {
    const next = result.descriptor; const old = known.get(next.revision);
    if (old && channelDescriptorCodec.stringify(old.descriptor) !== channelDescriptorCodec.stringify(next)) throw new ProtocolError('conflicting_revision', 'Channel descriptor conflicts with an already verified revision.');
    const previous = record.descriptor;
    if (previous && (previous.creator !== next.creator || previous.createdAt !== next.createdAt || canonicalJson([...previous.nonce]) !== canonicalJson([...next.nonce]))) throw new ProtocolError('immutable_field', 'Immutable channel fields changed.');
    if (previous?.status === 'closed' && next.revision > previous.revision) throw new ProtocolError('closed_channel', 'A closed channel cannot have a later descriptor revision.');
    known.set(next.revision, result); return previous && next.revision <= previous.revision ? record : { ...record, descriptor: next };
}

/** Verified evidence and projections commit together; all network reads occur before the CAS transaction. */
export class ChannelRepository {
    constructor(readonly store: MeshlineStore, readonly context: NetworkContext, readonly accountId: string) {}
    async get(channel: ChannelRef, signal?: AbortSignal): Promise<ChannelRecord> { validateChannelRef(channel); return decodeChannel((await this.store.read([channelKey(channel)], signal)).sets[0]![0]?.value, channel); }
    async post(post: ChannelPostRef, signal?: AbortSignal): Promise<ChannelPostInfo | undefined> {
        validateChannelPostRef(post); const rows = await this.store.read([channelKey(post.channel), postKey(post.channel, post.sequence)], signal); decodeChannel(rows.sets[0]![0]?.value, post.channel);
        const row = rows.sets[1]![0]; return row && postInfo(decodePost(row.value), post.channel);
    }
    async posts(channelId?: string, author?: string, signal?: AbortSignal): Promise<QueryReader<ChannelPostInfo>> {
        const rows = await this.store.read([{ collection: 'channel_posts', ...(channelId ? { prefix: `${channelId}|` } : {}) }, { collection: 'channels' }], signal);
        const channels = new Map(rows.sets[1]!.map(row => [row.key, { channelId: row.key, relayId: String(row.value.relayId) }]));
        const values = rows.sets[0]!.map(row => postInfo(decodePost(row.value), channels.get(row.key.split('|')[0]!)!)).filter((value): value is ChannelPostInfo => value !== undefined && (author === undefined || value.author === author));
        return snapshotReader(values);
    }
    async followed(relayId?: string, signal?: AbortSignal): Promise<readonly ChannelRecord[]> {
        const rows = (await this.store.read([{ collection: 'channels' }], signal)).sets[0]!;
        return rows.filter(row => row.value.isFollowed && (relayId === undefined || row.value.relayId === relayId)).map(row => decodeChannel(row.value, { channelId: row.key, relayId: String(row.value.relayId) }));
    }
    async follow(channel: ChannelRef, followed: boolean, signal?: AbortSignal): Promise<boolean> {
        return updateStore(this.store, [channelKey(channel)], snapshot => {
            const row = snapshot.sets[0]![0]; if (!row) return { mutations: [], result: false }; const current = decodeChannel(row.value, channel);
            return { mutations: current.isFollowed === followed ? [] : [put(channelKey(channel), encodeChannel({ ...current, isFollowed: followed }))], result: current.isFollowed !== followed };
        }, signal);
    }
    async saveDescriptor(channel: ChannelRef, result: ChannelResolveResult, operation?: ChannelOperation, signal?: AbortSignal): Promise<ChannelInfo | undefined> {
        verifyChannelDescriptor(result, this.context, channel); const key = descriptorKey(channel, result.descriptor.revision);
        return updateStore(this.store, [channelKey(channel), key, ...(operation ? [channelOperationKey(operation)] : [])], snapshot => {
            const before = decodeChannel(snapshot.sets[0]![0]?.value, channel); const known = new Map<number, ChannelResolveResult>();
            if (snapshot.sets[1]![0]) known.set(result.descriptor.revision, channelResolveResultCodec.decode(snapshot.sets[1]![0]!.value));
            const current = storeDescriptor(before, result, known); const mutations = [put(channelKey(channel), encodeChannel(current)), put(key, channelResolveResultCodec.encode(result))];
            if (operation && fingerprint(snapshot.sets[2]![0]?.value) === canonicalJson(encodeChannelOperation(operation))) mutations.push({ kind: 'delete', ...channelOperationKey(operation) });
            return { mutations, result: current !== before ? channelInfo(current) : undefined };
        }, signal);
    }
    async saveOperation(operation: ChannelOperation, signal?: AbortSignal): Promise<void> {
        validateChannelRef(operation.channel); validateChannelPayload(operation.payload, this.context); const key = channelOperationKey(operation);
        await updateStore(this.store, [key], snapshot => {
            if (snapshot.sets[0]!.length) throw new StateConflictError('An earlier operation on this channel resource remains unfinished. Start the component to recover it.');
            return { mutations: [put(key, encodeChannelOperation(operation))], result: undefined };
        }, signal);
    }
    async operations(signal?: AbortSignal): Promise<readonly ChannelOperation[]> { return (await this.store.read([{ collection: 'channel_operations' }], signal)).sets[0]!.map(readChannelOperation); }
    async hasOperation(operation: ChannelOperation, signal?: AbortSignal): Promise<boolean> { return fingerprint((await this.store.read([channelOperationKey(operation)], signal)).sets[0]![0]?.value) === canonicalJson(encodeChannelOperation(operation)); }
    async acknowledgePublication(operation: ChannelOperation, sequence: number): Promise<ChannelOperation> {
        requireSafeInteger(sequence, 1); if (operation.method !== 'channel.post') throw new ProtocolError('invalid_operation', 'Only publications return sequence acknowledgements.');
        const accepted = { ...operation, acceptedSequence: sequence }; const key = channelOperationKey(operation);
        await updateStore(this.store, [key], snapshot => {
            if (fingerprint(snapshot.sets[0]![0]?.value) !== canonicalJson(encodeChannelOperation(operation))) throw new StateConflictError('Publication changed before acknowledgement could be saved.');
            return { mutations: [put(key, encodeChannelOperation(accepted))], result: undefined };
        }); return accepted;
    }
    async confirmPublication(channel: ChannelRef, sequence: number, request: ChannelPost, signal?: AbortSignal): Promise<void> {
        const value = (await this.store.read([postKey(channel, sequence)], signal)).sets[0]![0]?.value;
        if (!value) throw new ProtocolError('missing_publication', 'Accepted publication is absent from the verified timeline.'); const post = decodePost(value);
        if (post.messageId !== undefined && (post.messageId !== request.messageId || post.author !== this.accountId || post.original && channelPostCodec.stringify(post.original) !== channelPostCodec.stringify(request))) throw new ProtocolError('conflicting_publication', 'Acknowledged sequence identifies another publication.');
    }
    async completeOperation(operation: ChannelOperation, deletion = false, signal?: AbortSignal): Promise<ChannelPostChange | undefined> {
        const channel = operation.channel; const sequence = deletion && operation.payload.kind === 'delete' ? operation.payload.value.targetSequence : undefined;
        return updateStore(this.store, [channelOperationKey(operation), channelKey(channel), ...(sequence ? [postKey(channel, sequence)] : [])], snapshot => {
            const mutations: StoreMutation[] = []; let result: ChannelPostChange | undefined;
            if (fingerprint(snapshot.sets[0]![0]?.value) !== canonicalJson(encodeChannelOperation(operation))) return { mutations, result };
            mutations.push({ kind: 'delete', ...channelOperationKey(operation) }, put(channelKey(channel), encodeChannel(decodeChannel(snapshot.sets[1]![0]?.value, channel))));
            if (sequence !== undefined) {
                const previous = snapshot.sets[2]![0]?.value; const post = previous ? decodePost(previous) : { sequence, appliedThrough: sequence, isDeleted: false };
                if (!post.isDeleted && post.post) result = { ref: { channel, sequence }, kind: 'deleted' };
                post.isDeleted = true; delete post.post; mutations.push(put(postKey(channel, sequence), encodePost(post)));
            }
            return { mutations, result };
        }, signal);
    }
    async publication(channel: ChannelRef, request: ChannelPost, signal?: AbortSignal): Promise<number | undefined> {
        const rows = (await this.store.read([{ collection: 'channel_posts', prefix: prefix(channel) }], signal)).sets[0]!;
        const post = rows.map(row => decodePost(row.value)).find(value => value.messageId === request.messageId && value.author === this.accountId);
        if (post?.original && channelPostCodec.stringify(post.original) !== channelPostCodec.stringify(request)) throw new ProtocolError('conflicting_publication', 'Synchronized publication differs from the exact pending request.'); return post?.sequence;
    }
    async readPage(channel: ChannelRef, query: ChannelReadQuery, reader: ChannelReader, advance: boolean, signal?: AbortSignal): Promise<{ page: ChannelReadPage; changes: ChannelChanges }> {
        validateChannelRef(channel); validateChannelReadQuery(query); if (query.channelId !== channel.channelId) throw new ProtocolError('invalid_binding', 'History query belongs to another channel.');
        const queries: RecordQuery[] = [channelKey(channel), ...['channel_posts', 'channel_descriptors', 'channel_events', 'channel_operations'].map(collection => ({ collection, prefix: prefix(channel) }))];
        const snapshot = await this.store.read(queries, signal); let record = decodeChannel(snapshot.sets[0]![0]?.value, channel); const beforeChannel = record;
        const known = new Map(snapshot.sets[2]!.map(row => { const result = channelResolveResultCodec.decode(row.value); verifyChannelDescriptor(result, this.context, channel); return [result.descriptor.revision, result] as const; }));
        const descriptors = new Map<number, ChannelResolveResult>(); const evidence = new Map<number, { event: ChannelEvent; certificate: DeviceCertificate }>();
        const applied = new Set<number>();
        const read = async (selection: ChannelReadQuery): Promise<ChannelReadPage> => {
            const page = channelReadPageCodec.decode(channelReadPageCodec.encode(await reader.read(selection, signal))); const certificates = validateChannelReadPage(page, selection, this.context);
            for (const entry of page.events) if (entry.payload.kind === 'descriptor') {
                const resolved = { descriptor: entry.payload.value, signerCertificate: certificates.get(entry.signerDeviceId)! }; verifyChannelDescriptor(resolved, this.context, channel);
                const previous = descriptors.get(entry.descriptorRev) ?? known.get(entry.descriptorRev);
                if (previous && channelDescriptorCodec.stringify(previous.descriptor) !== channelDescriptorCodec.stringify(resolved.descriptor)) throw new ProtocolError('conflicting_revision', 'Timeline descriptor conflicts with verified evidence.'); descriptors.set(entry.descriptorRev, resolved);
            }
            for (const entry of page.events) {
                let resolved = descriptors.get(entry.descriptorRev) ?? known.get(entry.descriptorRev);
                if (!resolved) { resolved = await reader.resolve(entry.descriptorRev, signal); verifyChannelDescriptor(resolved, this.context, channel); if (resolved.descriptor.revision !== entry.descriptorRev) throw new ProtocolError('invalid_revision', 'Relay returned a different descriptor revision.'); }
                descriptors.set(entry.descriptorRev, resolved); const certificate = certificates.get(entry.signerDeviceId)!; verifyChannelEvent(entry, certificate, resolved.descriptor, this.context, channel);
                const previous = evidence.get(entry.sequence)?.event ?? (snapshot.sets[3]!.find(row => row.key === eventKey(channel, entry.sequence).key)?.value.event ? channelEventCodec.decode(snapshot.sets[3]!.find(row => row.key === eventKey(channel, entry.sequence).key)!.value.event!) : undefined);
                if (previous && channelEventCodec.stringify(previous) !== channelEventCodec.stringify(entry)) throw new ProtocolError('conflicting_event', 'Timeline sequence conflicts with previously verified evidence.'); evidence.set(entry.sequence, { event: entry, certificate });
            }
            return page;
        };
        const page = await read(query); const posts = new Map<number, PostRecord>(); const originals = new Map<number, PostRecord | undefined>();
        const boundary = query.after ?? (page.events[0]?.sequence ?? 0) - 1;
        for (const event of page.events) {
            const sequence = target(event); if (sequence === undefined || posts.has(sequence)) continue;
            const saved = snapshot.sets[1]!.find(row => row.key === postKey(channel, sequence).key)?.value; const before = saved && decodePost(saved); originals.set(sequence, before);
            const post: PostRecord = saved ? decodePost(saved) : { sequence, appliedThrough: sequence - 1, isDeleted: false }; posts.set(sequence, post);
            const related = page.events.filter(entry => target(entry) === sequence);
            const deletion = related.find(entry => entry.payload.kind === 'delete');
            if (deletion) { post.isDeleted = true; delete post.post; post.appliedThrough = Math.max(post.appliedThrough, deletion.sequence); continue; }
            if (post.isDeleted) continue; if (post.post) post.appliedThrough = Math.max(post.appliedThrough, record.syncSequence);
            if (related.some(entry => entry.payload.kind === 'edit' && entry.sequence > post.appliedThrough) && !related.some(entry => entry.payload.kind === 'post') && post.appliedThrough < boundary) {
                let after = post.appliedThrough;
                for (;;) { const caught = await read({ channelId: channel.channelId, after });
                    for (const entry of caught.events) if (target(entry) === sequence && (entry.sequence <= boundary || entry.payload.kind === 'delete')) {
                        this.#apply(post, entry, evidence.get(entry.sequence)!.certificate); applied.add(entry.sequence);
                    }
                    if (!caught.hasMore || caught.events.at(-1)!.sequence >= boundary) break; after = caught.events.at(-1)!.sequence;
                }
            }
        }
        for (const entry of page.events) { const sequence = target(entry); if (sequence !== undefined) { this.#apply(posts.get(sequence)!, entry, evidence.get(entry.sequence)!.certificate); applied.add(entry.sequence); } }
        const changes: ChannelPostChange[] = []; const mutations: StoreMutation[] = [];
        for (const post of posts.values()) {
            if (!post.isDeleted && !post.post) throw new ProtocolError('missing_original', 'Original channel post is unavailable; the page remains uncommitted.');
            post.appliedThrough = Math.max(post.appliedThrough, page.events.at(-1)!.sequence); const before = originals.get(post.sequence); const info = postInfo(post, channel);
            if (!before?.isDeleted) { if (post.isDeleted && before?.post) changes.push({ ref: { channel, sequence: post.sequence }, kind: 'deleted' });
                else if (info && fingerprint(before?.post && channelPostCodec.encode(before.post)) !== fingerprint(post.post && channelPostCodec.encode(post.post))) changes.push({ ref: info.ref, kind: before?.post ? 'edited' : 'added', info }); }
            mutations.push(put(postKey(channel, post.sequence), encodePost(post)));
        }
        for (const result of [...descriptors.values()].sort((a, b) => a.descriptor.revision - b.descriptor.revision)) { record = storeDescriptor(record, result, known); mutations.push(put(descriptorKey(channel, result.descriptor.revision), channelResolveResultCodec.encode(result))); }
        if (advance && page.events.length) record = { ...record, syncSequence: Math.max(record.syncSequence, page.events.at(-1)!.sequence) };
        mutations.push(put(channelKey(channel), encodeChannel(record)));
        for (const { event, certificate } of evidence.values()) {
            mutations.push(put(eventKey(channel, event.sequence), { event: channelEventCodec.encode(event) }));
            // Catch-up verifies whole pages, but may project only an earlier subset
            // for selected posts. Confirmation must commit with the affected projection.
            if (!applied.has(event.sequence)) continue;
            for (const row of snapshot.sets[4]!) {
                const operation = readChannelOperation(row); const payload = operation.payload;
                if (payload.kind === 'post' && event.payload.kind === 'post' && payload.value.messageId === event.payload.value.messageId && certificate.account === this.accountId) {
                    if (operation.acceptedSequence !== undefined && operation.acceptedSequence !== event.sequence) throw new ProtocolError('invalid_sequence', 'Publication acknowledgement differs from its verified timeline sequence.');
                    if (canonicalJson(channelPayloadCodec.encode(payload)) !== canonicalJson(channelPayloadCodec.encode(event.payload))) throw new ProtocolError('conflicting_publication', 'Synchronized post conflicts with the pending publication.'); mutations.push({ ...channelOperationKey(operation), kind: 'delete' });
                } else if ((payload.kind === 'edit' || payload.kind === 'delete') && payload.kind === event.payload.kind && target(event) === payload.value.targetSequence
                    && (payload.kind === 'delete' || canonicalJson(channelPayloadCodec.encode(payload)) === canonicalJson(channelPayloadCodec.encode(event.payload)))) mutations.push({ ...channelOperationKey(operation), kind: 'delete' });
            }
        }
        const unique = [...new Map(mutations.map(mutation => [`${mutation.collection}|${mutation.key}`, mutation])).values()];
        await updateStore(this.store, queries, current => {
            if (snapshot.sets.some((rows, i) => !sameRows(rows, current.sets[i]!))) throw new StateConflictError('Channel state changed while history was being verified; retry with current state.');
            return { mutations: unique, result: undefined };
        }, signal);
        return { page, changes: { posts: changes, ...(record.descriptor !== beforeChannel.descriptor ? { channel: channelInfo(record) } : {}) } };
    }
    #apply(post: PostRecord, entry: ChannelEvent, certificate: DeviceCertificate): void {
        const payload = entry.payload;
        if (payload.kind === 'post') {
            if (post.messageId !== undefined) { if (post.messageId !== payload.value.messageId || post.author !== certificate.account || post.original && channelPostCodec.stringify(post.original) !== channelPostCodec.stringify(payload.value)) throw new ProtocolError('conflicting_post', 'Original channel publication changed.'); return; }
            post.messageId = payload.value.messageId; post.author = certificate.account; post.acceptedAt = entry.acceptedAt; post.original = payload.value; if (!post.isDeleted) post.post = payload.value;
        } else if (payload.kind === 'delete') { post.isDeleted = true; delete post.post; }
        else if (payload.kind === 'edit' && !post.isDeleted && entry.sequence > post.appliedThrough) {
            if (!post.post) throw new ProtocolError('missing_original', 'Original channel post is unavailable; the page remains uncommitted.'); post.post = applyChannelPostEdit(post.post, payload.value);
        }
        post.appliedThrough = Math.max(post.appliedThrough, entry.sequence);
    }
}
