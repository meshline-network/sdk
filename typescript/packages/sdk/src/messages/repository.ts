import { ProtocolError, StateConflictError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId, validateRelayId } from '../identity/neo.js';
import type { SecretProtector } from '../interactions.js';
import { directMessageCodec, messagePayloadCodec, validateDirectMessage, type DirectMessage, type MessageEnvelope, type MessageTimelineEntry } from '../models/messages.js';
import type { NetworkContext } from '../protocol/context.js';
import { decodeBase64Url, encodeBase64Url } from '../protocol/encoding.js';
import { canonicalBytes, canonicalJson, parseJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { requireBatchCount, type MeshlineStore, type QueryReader, type RecordKey, type RecordQuery, type StoredRecord, type StoreMutation, type StoreSnapshot } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { encodeOutbox, outboxKey, type OutboxRecord } from './outbox.js';
import { historyIndex, historyQuery, historyReader, historySelection, validateHistoryRows, type HistoryRange } from '../models/history.js';

export interface MessageKey { readonly sender: string; readonly messageId: string }
export interface MessageInfo extends DirectMessage {
    /** Monotonically assigned position in this database's account-message stream; gaps are allowed. Not comparable across databases or with relay sequences. */
    readonly localSequence: number;
    readonly key: MessageKey; readonly senderDeviceId: string; readonly recipient: string; readonly createdAt: number;
}
type MessageAcceptance = { readonly advanced: boolean; readonly inserted: false }
    | { readonly advanced: true; readonly inserted: true; readonly localSequence: number };
export interface AccountMessage {
    readonly localSequence: number; readonly sender: string; readonly senderDeviceId: string; readonly recipient: string; readonly messageId: string; readonly payload: JsonObject;
}
export interface StoredMessage extends MessageKey {
    readonly senderDeviceId: string; readonly recipient: string; readonly createdAt: number; readonly payloadType: string;
    readonly payload?: JsonObject; readonly protectedPayload?: string; readonly localSequence: number;
}
export type PreparedMessage = Omit<StoredMessage, 'localSequence'>;
export interface TimelineProgress { readonly sequence: number; readonly hasRetentionGap: boolean; readonly lastSynchronizedAt?: number }
export interface RecordGuard { readonly query: RecordKey; readonly value: JsonObject | undefined }
/** Synchronous business validation and mutations join the same compare-and-commit as messages and cursors. */
export interface MessageEffects {
    readonly queries: readonly RecordQuery[];
    plan(snapshot: StoreSnapshot): readonly StoreMutation[];
    /** Owning runtime notification only; it must return immediately without awaiting application observers. */
    committed?(snapshot: StoreSnapshot, mutations: readonly StoreMutation[]): void;
}
export interface MessageRepositoryOptions {
    readonly store: MeshlineStore; readonly context: NetworkContext; readonly accountId: string; readonly deviceId: () => string;
    readonly clock: RuntimeClock; readonly secretProtector?: SecretProtector;
}
export const messageKey = (value: MessageKey): RecordKey => {
    validateAccountId(value.sender); validateIdentifier('message', value.messageId); return { collection: 'messages', key: `${value.sender}|${value.messageId}` };
};
export const timelineKey = (relayId: string): RecordKey => { validateRelayId(relayId); return { collection: 'message_timelines', key: relayId }; };
const sequenceKey: RecordKey = { collection: 'message_meta', key: 'local_sequence' };
const historyMarker: RecordKey = { collection: 'message_meta', key: 'history_index_v1' };
const secretTypes = new Set(['meshline.account.group.state.sync', 'meshline.account.group.history_secret.sync']);
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
function progress(value: JsonObject | undefined): TimelineProgress {
    if (!value) return { sequence: -1, hasRetentionGap: false };
    requireSafeInteger(value.sequence, -1);
    if (typeof value.hasRetentionGap !== 'boolean') throw new ProtocolError('invalid_storage', 'Invalid persisted timeline retention flag.');
    if (value.lastSynchronizedAt !== undefined) requireSafeInteger(value.lastSynchronizedAt, 0);
    return { sequence: value.sequence, hasRetentionGap: value.hasRetentionGap, ...(value.lastSynchronizedAt === undefined ? {} : { lastSynchronizedAt: value.lastSynchronizedAt }) };
}
function encodeMessage(value: StoredMessage): JsonObject {
    return { sender: value.sender, messageId: value.messageId, senderDeviceId: value.senderDeviceId, recipient: value.recipient, createdAt: value.createdAt,
        payloadType: value.payloadType, localSequence: value.localSequence, ...(value.payload === undefined ? {} : { payload: value.payload }),
        ...(value.protectedPayload === undefined ? {} : { protectedPayload: value.protectedPayload }) };
}
function decodeMessage(row: StoredRecord): StoredMessage {
    const value = row.value;
    for (const name of ['sender', 'messageId', 'senderDeviceId', 'recipient', 'payloadType']) if (typeof value[name] !== 'string') throw new ProtocolError('invalid_storage', `Stored message has no ${name}.`);
    const key = { sender: value.sender as string, messageId: value.messageId as string };
    if (messageKey(key).key !== row.key) throw new ProtocolError('invalid_storage', 'Stored message identity differs from its key.');
    validateAccountId(value.recipient as string); validateIdentifier('device', value.senderDeviceId as string); requireSafeInteger(value.createdAt, 0); requireSafeInteger(value.localSequence, 1);
    const secret = secretTypes.has(value.payloadType as string);
    if (secret ? value.payload !== undefined || typeof value.protectedPayload !== 'string' : value.protectedPayload !== undefined || value.payload === undefined)
        throw new ProtocolError('invalid_storage', 'Message payload protection differs from its declared type.');
    const payload = secret ? undefined : messagePayloadCodec.decode(value.payload!);
    if (payload && payload['$type'] !== value.payloadType) throw new ProtocolError('invalid_storage', 'Stored message type differs from its payload.');
    if (secret && decodeBase64Url(value.protectedPayload as string).length === 0) throw new ProtocolError('invalid_storage', 'Protected payload cannot be empty.');
    return { ...key, senderDeviceId: value.senderDeviceId as string, recipient: value.recipient as string, createdAt: value.createdAt, payloadType: value.payloadType as string,
        localSequence: value.localSequence, ...(payload ? { payload } : { protectedPayload: value.protectedPayload as string }) };
}

/** Internal persistence; callers must verify envelope signatures, decryption and business authorization before preparing an incoming record. */
export class MessageRepository {
    constructor(readonly options: MessageRepositoryOptions) {}
    #belongsToAccount(value: Pick<StoredMessage, 'sender' | 'recipient'>): void {
        if (value.sender !== this.options.accountId && value.recipient !== this.options.accountId) throw new ProtocolError('invalid_identity', 'Message does not belong to this account.');
    }
    #purpose(value: MessageKey): string {
        const deviceId = this.options.deviceId(); validateIdentifier('device', deviceId);
        return `Meshline/${this.options.context}/${this.options.accountId}/${deviceId}/messages/${value.sender}/${value.messageId}`;
    }
    async prepare(envelope: MessageEnvelope, source: JsonObject, signal?: AbortSignal): Promise<PreparedMessage> {
        throwIfAborted(signal);
        const payload = messagePayloadCodec.decode(source); const type = payload['$type'] as string;
        if (type === 'meshline.message.direct') validateDirectMessage(directMessageCodec.decode(payload));
        const value = { sender: envelope.from, messageId: envelope.messageId, senderDeviceId: envelope.fromDeviceId, recipient: envelope.to, createdAt: envelope.createdAt, payloadType: type };
        messageKey(value); this.#belongsToAccount(value); validateIdentifier('device', value.senderDeviceId); requireSafeInteger(value.createdAt, 0);
        if (!secretTypes.has(type)) return { ...value, payload };
        const protector = this.options.secretProtector; if (!protector) throw new ProtocolError('protector_required', 'A secret protector is required to store group synchronization secrets.');
        const plaintext = canonicalBytes(payload);
        try {
            const protectedData = await protector.protect(plaintext, this.#purpose(value), signal); throwIfAborted(signal);
            if (!(protectedData instanceof Uint8Array) || !protectedData.length) throw new ProtocolError('invalid_protection', 'Secret protector returned no protected payload.');
            return { ...value, protectedPayload: encodeBase64Url(protectedData) };
        } finally { plaintext.fill(0); }
    }
    async #payload(value: StoredMessage, signal?: AbortSignal): Promise<JsonObject> {
        this.#belongsToAccount(value); throwIfAborted(signal);
        if (value.payload) return messagePayloadCodec.decode(value.payload);
        const protector = this.options.secretProtector; if (!protector) throw new ProtocolError('protector_required', 'A secret protector is required to read group synchronization secrets.');
        const plaintext = await protector.unprotect(decodeBase64Url(value.protectedPayload!), this.#purpose(value), signal);
        try {
            throwIfAborted(signal); const payload = messagePayloadCodec.decode(parseJson(plaintext));
            if (payload['$type'] !== value.payloadType) throw new ProtocolError('invalid_storage', 'Unprotected payload type differs from the stored type.');
            return payload;
        } finally { plaintext.fill(0); }
    }
    async get(key: MessageKey, signal?: AbortSignal): Promise<MessageInfo | undefined> {
        const row = (await this.options.store.read([messageKey(key)], signal)).sets[0]![0];
        if (!row) return undefined; const value = decodeMessage(row); this.#belongsToAccount(value);
        if (value.payloadType !== 'meshline.message.direct') return undefined;
        return this.#info(value);
    }
    async contains(key: MessageKey, signal?: AbortSignal): Promise<boolean> {
        const row = (await this.options.store.read([messageKey(key)], signal)).sets[0]![0];
        if (!row) return false; this.#belongsToAccount(decodeMessage(row)); return true;
    }
    /** A previously committed message can advance another relay's cursor without needing historical decryption keys again. */
    async acceptKnown(relayId: string, entry: MessageTimelineEntry, hasRetentionGap: boolean, signal?: AbortSignal): Promise<boolean> {
        const key = timelineKey(relayId); requireSafeInteger(entry.sequence, 0); requireSafeInteger(entry.acceptedAt, 0);
        const now = this.options.clock.nowSeconds(); requireSafeInteger(now, 0);
        return updateStore(this.options.store, [key, messageKey({ sender: entry.envelope.from, messageId: entry.envelope.messageId })], snapshot => {
            const known = snapshot.sets[1]![0]; if (!known) return { mutations: [], result: false };
            this.#belongsToAccount(decodeMessage(known)); const current = progress(snapshot.sets[0]![0]?.value);
            return { mutations: current.sequence >= entry.sequence ? [] : [{ kind: 'put', ...key, value: { sequence: entry.sequence, hasRetentionGap: current.hasRetentionGap || hasRetentionGap, lastSynchronizedAt: now } }], result: true };
        }, signal);
    }
    #info(value: StoredMessage): MessageInfo {
        this.#belongsToAccount(value); const direct = directMessageCodec.decode(value.payload!); validateDirectMessage(direct);
        return { ...direct, localSequence: value.localSequence, key: { sender: value.sender, messageId: value.messageId }, senderDeviceId: value.senderDeviceId, recipient: value.recipient, createdAt: value.createdAt };
    }
    async history(peer?: string, signal?: AbortSignal, bounds: HistoryRange = {}): Promise<QueryReader<MessageInfo>> {
        if (peer !== undefined) validateAccountId(peer);
        const selected = historySelection(bounds);
        await this.#ensureHistoryIndex(signal);
        const prefix = `${peer ?? '*'}|`; const query = historyQuery('message_history', prefix, selected);
        const reader = await this.options.store.openQuery(query, signal);
        return historyReader(reader, query, async (indexes, readSignal) => {
            const records = (await this.options.store.read(indexes.map(row => messageKey({ sender: String(row.value.sender), messageId: String(row.value.messageId) })), readSignal)).sets;
            return records.map((rows, index) => {
                if (rows.length !== 1) throw new ProtocolError('invalid_storage', 'History index references a missing message.');
                const value = decodeMessage(rows[0]!);
                if (value.payloadType !== 'meshline.message.direct' || prefix + historyIndex(value.localSequence) !== indexes[index]!.key
                    || peer !== undefined && !(value.sender === this.options.accountId && value.recipient === peer || value.sender === peer && value.recipient === this.options.accountId))
                    throw new ProtocolError('invalid_storage', 'History index differs from its message.');
                return this.#info(value);
            });
        });
    }
    async #ensureHistoryIndex(signal?: AbortSignal): Promise<void> {
        // Backfill older stores in bounded atomic batches. New writes maintain both indexes in their message transaction.
        for (;;) {
            throwIfAborted(signal);
            const marker = (await this.options.store.read([historyMarker], signal)).sets[0]![0]?.value;
            if (marker?.complete === true) return;
            if (marker?.after !== undefined && typeof marker.after !== 'string') throw new ProtocolError('invalid_storage', 'Invalid history index checkpoint.');
            const query = { collection: 'messages', ...(marker?.after === undefined ? {} : { after: marker.after as string }), limit: 256 };
            await updateStore(this.options.store, [historyMarker, query], snapshot => {
                if (fingerprint(snapshot.sets[0]![0]?.value) !== fingerprint(marker)) return { mutations: [], result: undefined };
                const rows = snapshot.sets[1]!; validateHistoryRows(rows, query);
                const mutations = rows.flatMap(row => { const value = decodeMessage(row); this.#belongsToAccount(value); return historyMutations(value, this.options.accountId); });
                mutations.push({ kind: 'put', ...historyMarker, value: { complete: rows.length < 256, ...(rows.length ? { after: rows.at(-1)!.key } : {}) } });
                return { mutations, result: undefined };
            }, signal);
        }
    }
    async readTimeline(after: number, count: number, signal?: AbortSignal): Promise<readonly AccountMessage[]> {
        requireSafeInteger(after, 0); requireBatchCount(count);
        const snapshot = await this.options.store.read([{ collection: 'messages' }], signal);
        const records = snapshot.sets[0]!.map(decodeMessage).filter(value => value.localSequence > after).sort((a, b) => a.localSequence - b.localSequence).slice(0, count);
        const results: AccountMessage[] = [];
        for (const value of records) results.push({ localSequence: value.localSequence, sender: value.sender, senderDeviceId: value.senderDeviceId, recipient: value.recipient,
            messageId: value.messageId, payload: await this.#payload(value, signal) });
        return results;
    }
    async getProgress(relayId: string, signal?: AbortSignal): Promise<TimelineProgress> {
        return progress((await this.options.store.read([timelineKey(relayId)], signal)).sets[0]![0]?.value);
    }
    async observeRelay(relayId: string, signal?: AbortSignal): Promise<void> {
        const key = timelineKey(relayId);
        await updateStore(this.options.store, [key], snapshot => ({ mutations: snapshot.sets[0]!.length ? [] : [{ kind: 'put', ...key, value: { sequence: -1, hasRetentionGap: false } }], result: undefined }), signal);
    }
    /** The caller provides state fingerprints captured before signing; unrelated store writes may be retried without signing again. */
    async enqueue(outbox: OutboxRecord, message: PreparedMessage | undefined, guards: readonly RecordGuard[], effects?: MessageEffects, signal?: AbortSignal): Promise<void> {
        const envelope = outbox.request.envelope; const key = outboxKey(envelope.messageId);
        if (envelope.from !== this.options.accountId || outbox.state !== 'queued' || outbox.acceptedAt !== undefined) throw new ProtocolError('invalid_outbox', 'Only new local queued messages may be enqueued.');
        if (outbox.isDirect !== Boolean(message) || message && message.payloadType !== 'meshline.message.direct') throw new ProtocolError('invalid_outbox', 'Only direct outgoing messages are inserted into local history at enqueue time.');
        if (message && !matchesEnvelope(message, envelope)) throw new ProtocolError('invalid_outbox', 'Local message identity differs from the outgoing envelope.');
        const queries: RecordQuery[] = [key, messageKey({ sender: envelope.from, messageId: envelope.messageId }), sequenceKey, ...guards.map(value => value.query), ...effects?.queries ?? []];
        const document = encodeOutbox(outbox);
        const committed = await updateStore(this.options.store, queries, snapshot => {
            if (snapshot.sets[0]!.length || snapshot.sets[1]!.length) throw new StateConflictError('Outgoing message ID already exists.');
            guards.forEach((guard, index) => { if (fingerprint(snapshot.sets[index + 3]![0]?.value) !== fingerprint(guard.value)) throw new StateConflictError('Message authorization changed while preparing the encrypted request.'); });
            const mutations: StoreMutation[] = [{ kind: 'put', ...key, value: document }];
            if (message) mutations.push(...insertMessage(message, snapshot.sets[2]![0]?.value, this.options.accountId).mutations);
            const effectSnapshot = { ...snapshot, sets: snapshot.sets.slice(3 + guards.length) }; const effectMutations = effects?.plan(effectSnapshot) ?? [];
            mutations.push(...effectMutations);
            return { mutations, result: { snapshot: effectSnapshot, mutations: effectMutations } };
        }, signal);
        effects?.committed?.(committed.snapshot, committed.mutations);
    }
    async accept(relayId: string, entry: MessageTimelineEntry, message: PreparedMessage, hasRetentionGap: boolean, effects?: MessageEffects, signal?: AbortSignal): Promise<MessageAcceptance> {
        if (!matchesEnvelope(message, entry.envelope)) throw new ProtocolError('invalid_message', 'Prepared message identity differs from the timeline envelope.');
        this.#belongsToAccount(message); requireSafeInteger(entry.sequence, 0); requireSafeInteger(entry.acceptedAt, 0);
        const key = timelineKey(relayId); const now = this.options.clock.nowSeconds(); requireSafeInteger(now, 0);
        const result = await updateStore<MessageAcceptance & { snapshot?: StoreSnapshot; mutations?: readonly StoreMutation[] }>(this.options.store, [key, messageKey(message), sequenceKey, ...effects?.queries ?? []], snapshot => {
            const current = progress(snapshot.sets[0]![0]?.value);
            if (current.sequence >= entry.sequence) return { mutations: [], result: { advanced: false, inserted: false } };
            const inserted = !snapshot.sets[1]!.length;
            const mutations: StoreMutation[] = [{ kind: 'put', ...key, value: { sequence: entry.sequence, hasRetentionGap: hasRetentionGap || current.hasRetentionGap, lastSynchronizedAt: now } }];
            if (!inserted) return { mutations, result: { advanced: true, inserted: false } };
            const effectSnapshot = { ...snapshot, sets: snapshot.sets.slice(3) }; const effectMutations = effects?.plan(effectSnapshot) ?? [];
            const insertion = insertMessage(message, snapshot.sets[2]![0]?.value, this.options.accountId);
            mutations.push(...effectMutations, ...insertion.mutations);
            return { mutations, result: { advanced: true, inserted: true, localSequence: insertion.localSequence, snapshot: effectSnapshot, mutations: effectMutations } };
        }, signal);
        if (!result.inserted) return { advanced: result.advanced, inserted: false };
        effects?.committed?.(result.snapshot!, result.mutations!); return { advanced: true, inserted: true, localSequence: result.localSequence };
    }
    /** Rejected authenticated entries advance only when the receiver classifies the error as permanent protocol rejection. */
    async reject(relayId: string, sequence: number, error: ProtocolError, hasRetentionGap: boolean, signal?: AbortSignal): Promise<boolean> {
        requireSafeInteger(sequence, 0); const key = timelineKey(relayId); const now = this.options.clock.nowSeconds(); requireSafeInteger(now, 0);
        return updateStore(this.options.store, [key], snapshot => {
            const current = progress(snapshot.sets[0]![0]?.value); if (current.sequence >= sequence) return { mutations: [], result: false };
            return { mutations: [
                { kind: 'put', ...key, value: { sequence, hasRetentionGap: hasRetentionGap || current.hasRetentionGap, lastSynchronizedAt: now } },
                { kind: 'put', collection: 'message_rejections', key: `${relayId}|${sequence.toString().padStart(16, '0')}`, value: { relayId, sequence, code: error.code, message: error.message, rejectedAt: now } },
            ], result: true };
        }, signal);
    }
    async acceptEmptyPage(relayId: string, hasRetentionGap: boolean, signal?: AbortSignal): Promise<void> {
        const key = timelineKey(relayId); const now = this.options.clock.nowSeconds(); requireSafeInteger(now, 0);
        await updateStore(this.options.store, [key], snapshot => {
            const current = progress(snapshot.sets[0]![0]?.value);
            return { mutations: [{ kind: 'put', ...key, value: { sequence: current.sequence, hasRetentionGap: current.hasRetentionGap || hasRetentionGap, lastSynchronizedAt: now } }], result: undefined };
        }, signal);
    }
}
function historyMutations(value: StoredMessage, accountId: string): StoreMutation[] {
    if (value.payloadType !== 'meshline.message.direct') return [];
    const peer = value.sender === accountId ? value.recipient : value.sender;
    return ['*', peer].map(prefix => ({ kind: 'put', collection: 'message_history', key: `${prefix}|${historyIndex(value.localSequence)}`,
        value: { sender: value.sender, messageId: value.messageId, localSequence: value.localSequence } }));
}
function insertMessage(value: PreparedMessage, meta: JsonObject | undefined, accountId: string): { localSequence: number; mutations: StoreMutation[] } {
    const previous = meta?.sequence ?? 0; requireSafeInteger(previous, 0); const sequence = previous + 1; requireSafeInteger(sequence, 1);
    const stored = { ...value, localSequence: sequence };
    return { localSequence: sequence, mutations: [{ kind: 'put', ...messageKey(value), value: encodeMessage(stored) }, { kind: 'put', ...sequenceKey, value: { sequence } }, ...historyMutations(stored, accountId)] };
}
function matchesEnvelope(value: PreparedMessage, envelope: MessageEnvelope): boolean {
    return value.sender === envelope.from && value.messageId === envelope.messageId && value.senderDeviceId === envelope.fromDeviceId && value.recipient === envelope.to && value.createdAt === envelope.createdAt;
}
/** Materialized immutable query results; caller mutation of a returned page cannot affect later pages. */
export function snapshotReader<T>(source: readonly T[]): QueryReader<T> {
    let values: readonly T[] | undefined = source; let offset = 0;
    return {
        async readNext(count, signal) { throwIfAborted(signal); requireBatchCount(count); if (!values) throw new ProtocolError('disposed', 'Query reader is disposed.');
            const result = values.slice(offset, offset + count); offset += result.length; return result; },
        async dispose() { values = undefined; },
    };
}
