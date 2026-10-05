import { ProtocolError, StateConflictError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateRelayId } from '../identity/neo.js';
import { messageSendRequestCodec, validateMessageDeliveryStatus, validateMessageSendRequest, type MessageDeliveryStatus, type MessageSendRequest } from '../models/messages.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import type { MeshlineStore, RecordKey, RecordQuery, StoredRecord, StoreMutation, StoreSnapshot } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { RelayError } from '../transport/relay-error.js';

export type MessageSendState = 'queued' | 'submitting' | 'submissionUnknown' | 'relayAccepted' | 'targetAccepted' | 'failed' | 'canceled';
export interface MessageSendStatus {
    readonly messageId: string; readonly recipient: string; readonly createdAt: number; readonly state: MessageSendState;
    readonly acceptedRelayId?: string; readonly acceptedAt?: number; readonly errorMessage?: string;
}
export interface OutboxRecord {
    readonly request: MessageSendRequest; readonly relayId: string; readonly state: MessageSendState;
    readonly nextAttemptAt: number; readonly isDirect: boolean; readonly acceptedAt?: number; readonly errorMessage?: string;
}
export interface OutboxChange { readonly previous: OutboxRecord; readonly current: OutboxRecord }
const states = new Set<MessageSendState>(['queued', 'submitting', 'submissionUnknown', 'relayAccepted', 'targetAccepted', 'failed', 'canceled']);
const pending = new Set<MessageSendState>(['queued', 'submissionUnknown', 'relayAccepted']);
const terminal = new Set<MessageSendState>(['targetAccepted', 'failed', 'canceled']);
const sendHistoryLimit = 1000;
export const outboxKey = (messageId: string): RecordKey => ({ collection: 'message_outbox', key: messageId });
export function encodeOutbox(value: OutboxRecord): JsonObject {
    return { request: messageSendRequestCodec.encode(value.request), relayId: value.relayId, state: value.state, nextAttemptAt: value.nextAttemptAt, isDirect: value.isDirect,
        ...(value.acceptedAt === undefined ? {} : { acceptedAt: value.acceptedAt }), ...(value.errorMessage === undefined ? {} : { errorMessage: value.errorMessage }) };
}
export function decodeOutbox(row: StoredRecord): OutboxRecord {
    const value = row.value; const request = messageSendRequestCodec.decode(requireObject(value.request!)); validateMessageSendRequest(request, 0);
    if (request.envelope.messageId !== row.key || typeof value.relayId !== 'string' || typeof value.state !== 'string' || !states.has(value.state as MessageSendState)
        || typeof value.isDirect !== 'boolean' || value.errorMessage !== undefined && typeof value.errorMessage !== 'string') throw new ProtocolError('invalid_storage', 'Invalid persisted message outbox entry.');
    validateRelayId(value.relayId); requireSafeInteger(value.nextAttemptAt, 0);
    if (value.acceptedAt !== undefined) requireSafeInteger(value.acceptedAt, 0);
    const state = value.state as MessageSendState;
    if (['relayAccepted', 'targetAccepted'].includes(state) && value.acceptedAt === undefined
        || ['queued', 'submitting', 'submissionUnknown', 'canceled'].includes(state) && value.acceptedAt !== undefined)
        throw new ProtocolError('invalid_storage', 'Outbox state conflicts with its acceptance evidence.');
    return { request, relayId: value.relayId, state, nextAttemptAt: value.nextAttemptAt, isDirect: value.isDirect,
        ...(value.acceptedAt === undefined ? {} : { acceptedAt: value.acceptedAt }), ...(value.errorMessage === undefined ? {} : { errorMessage: value.errorMessage as string }) };
}
export function outboxStatus(value: OutboxRecord): MessageSendStatus {
    return { messageId: value.request.envelope.messageId, recipient: value.request.envelope.to, createdAt: value.request.envelope.createdAt, state: value.state,
        ...(value.acceptedAt === undefined ? {} : { acceptedAt: value.acceptedAt, acceptedRelayId: value.relayId }), ...(value.errorMessage === undefined ? {} : { errorMessage: value.errorMessage }) };
}
/** Resolved before a queued message is claimed; requests themselves must not retry business operations internally. */
export interface OutboxTransport {
    getHome(signal?: AbortSignal): Promise<string>;
    prepare(relayId: string, signal?: AbortSignal): Promise<{
        send(request: MessageSendRequest, signal?: AbortSignal): Promise<MessageDeliveryStatus>;
        status(messageId: string, signal?: AbortSignal): Promise<MessageDeliveryStatus>;
    }>;
    currentHome(): string | undefined;
}
export interface OutboxEffects {
    /** Additional rows needed for atomic contact/grant delivery effects. First snapshot set is always the outbox record. */
    readonly queries: readonly RecordQuery[];
    plan(snapshot: StoreSnapshot, change: OutboxChange): readonly StoreMutation[];
    /** Invoked once after a successful commit, with that transaction's input and exact effects. */
    committed?(snapshot: StoreSnapshot, mutations: readonly StoreMutation[]): void;
}
/** Internal durable delivery engine. Its owner supplies lifecycle, authorization, events and enqueue transactions. */
export class MessageOutbox {
    readonly #gate = new AsyncGate();
    constructor(readonly store: MeshlineStore, readonly clock: RuntimeClock, readonly transport: OutboxTransport, readonly effects?: OutboxEffects,
        readonly changed?: (status: MessageSendStatus) => void) {}

    async recover(signal?: AbortSignal): Promise<readonly OutboxChange[]> {
        const snapshot = await this.store.read([{ collection: 'message_outbox' }], signal); const changes: OutboxChange[] = [];
        for (const row of snapshot.sets[0]!) {
            if (decodeOutbox(row).state !== 'submitting') continue;
            const change = await this.#update(row.key, current => current.state === 'submitting' ? { ...current, state: 'submissionUnknown', nextAttemptAt: this.clock.nowSeconds() } : undefined, signal);
            if (change) changes.push(change);
        }
        await updateStore(this.store, [{ collection: 'message_outbox' }], snapshot => ({ mutations: this.#prune(snapshot.sets[0]!.map(decodeOutbox)), result: undefined }), signal);
        return changes;
    }
    async get(messageId: string, signal?: AbortSignal): Promise<OutboxRecord | undefined> {
        validateIdentifier('message', messageId); const row = (await this.store.read([outboxKey(messageId)], signal)).sets[0]![0]; return row && decodeOutbox(row);
    }
    async cancel(messageId: string, signal?: AbortSignal): Promise<OutboxChange | undefined> {
        validateIdentifier('message', messageId);
        return this.#update(messageId, value => value.state === 'queued' ? { ...value, state: 'canceled', errorMessage: undefined } : undefined, signal);
    }
    async due(signal?: AbortSignal): Promise<readonly string[]> {
        const now = this.clock.nowSeconds(); const snapshot = await this.store.read([{ collection: 'message_outbox' }], signal);
        return snapshot.sets[0]!.map(decodeOutbox).filter(value => pending.has(value.state) && value.nextAttemptAt <= now)
            .sort((a, b) => a.request.envelope.createdAt - b.request.envelope.createdAt || ordinal(a.request.envelope.messageId, b.request.envelope.messageId))
            .map(value => value.request.envelope.messageId);
    }
    /** Errors leave a durable retry state and are returned for visible reporting by the owning runtime. */
    process(messageId: string, signal?: AbortSignal): Promise<{ readonly changes: readonly OutboxChange[]; readonly error?: unknown }> {
        return this.#gate.run(() => this.#process(messageId, signal), signal);
    }
    async #process(messageId: string, signal?: AbortSignal): Promise<{ readonly changes: readonly OutboxChange[]; readonly error?: unknown }> {
        let record = await this.get(messageId, signal); const changes: OutboxChange[] = [];
        if (!record || !pending.has(record.state) || record.nextAttemptAt > this.clock.nowSeconds()) return { changes };
        const previousAcceptancePossible = record.state !== 'queued';
        let submitted = false;
        let acceptedAt = record.acceptedAt;
        try {
            const home = record.state === 'relayAccepted' ? undefined : await this.transport.getHome(signal);
            if (home !== undefined) validateRelayId(home);
            const relayId = record.state === 'queued' ? home! : record.relayId;
            const relay = await this.transport.prepare(relayId, signal); throwIfAborted(signal);
            if (record.state === 'queued') {
                if (this.transport.currentHome() !== relayId) throw new StateConflictError('Home relay changed before claiming the queued message.');
                const claimed = await this.#update(messageId, current => current.state === 'queued' && sameRequest(current, record!) ? { ...current, state: 'submitting', relayId } : undefined, signal);
                if (!claimed) return { changes };
                changes.push(claimed); record = claimed.current;
            }
            const oldRelay = home !== undefined && record.relayId !== this.transport.currentHome();
            if (oldRelay && record.state === 'submitting') {
                const change = await this.#replace(record, { ...record, state: 'queued', nextAttemptAt: this.clock.nowSeconds() }); if (change) changes.push(change);
                return { changes };
            }
            submitted = record.state !== 'relayAccepted' && !oldRelay;
            const delivery = submitted ? await relay.send(messageSendRequestCodec.decode(messageSendRequestCodec.encode(record.request)), signal) : await relay.status(messageId, signal);
            validateMessageDeliveryStatus(delivery);
            if (record.acceptedAt !== undefined && record.acceptedAt !== delivery.acceptedAt) throw new ProtocolError('invalid_delivery', 'Relay changed the original message acceptance time.');
            acceptedAt = delivery.acceptedAt;
            const current = { ...record, state: delivery.status === 'delivering' ? 'relayAccepted' as const : delivery.status === 'target_accepted' ? 'targetAccepted' as const : 'failed' as const,
                acceptedAt: delivery.acceptedAt, errorMessage: delivery.error?.message, nextAttemptAt: this.#retryAt() };
            const change = await this.#replace(record, current); if (change) changes.push(change);
            return { changes };
        } catch (error) {
            // A cancellation can interrupt the request after Relay acceptance; persistence deliberately ignores its signal.
            const definitive = submitted && !previousAcceptancePossible && error instanceof RelayError && error.isDefinitiveRejection;
            const state = record.state === 'queued' ? 'queued' : acceptedAt !== undefined ? 'relayAccepted' : definitive ? 'failed' : 'submissionUnknown';
            const current = { ...record, state, ...(acceptedAt === undefined ? {} : { acceptedAt }), errorMessage: error instanceof Error ? error.message : String(error), nextAttemptAt: this.#retryAt() } as const;
            const change = await this.#replace(record, current); if (change) changes.push(change);
            return { changes, error };
        }
    }
    #retryAt(): number { const result = this.clock.nowSeconds() + 15; requireSafeInteger(result, 0); return result; }
    #prune(records: readonly OutboxRecord[]): readonly StoreMutation[] {
        return records.filter(value => terminal.has(value.state))
            .sort((a, b) => b.request.envelope.createdAt - a.request.envelope.createdAt || ordinal(b.request.envelope.messageId, a.request.envelope.messageId))
            .slice(sendHistoryLimit).map(value => ({ kind: 'delete', ...outboxKey(value.request.envelope.messageId) }));
    }
    #replace(previous: OutboxRecord, current: Omit<OutboxRecord, 'errorMessage'> & { readonly errorMessage?: string | undefined }): Promise<OutboxChange | undefined> {
        const fingerprint = canonicalJson(encodeOutbox(previous));
        return this.#update(previous.request.envelope.messageId, saved => canonicalJson(encodeOutbox(saved)) === fingerprint ? current : undefined);
    }
    async #update(messageId: string, update: (record: OutboxRecord) => (Omit<OutboxRecord, 'errorMessage'> & { readonly errorMessage?: string | undefined }) | undefined, signal?: AbortSignal): Promise<OutboxChange | undefined> {
        const result = await updateStore(this.store, [outboxKey(messageId), ...(this.effects?.queries ?? []), { collection: 'message_outbox' }], snapshot => {
            const row = snapshot.sets[0]![0]; if (!row) return { mutations: [], result: undefined };
            const previous = decodeOutbox(row); const updated = update(previous); if (!updated) return { mutations: [], result: undefined };
            const { errorMessage, ...rest } = updated; const current: OutboxRecord = { ...rest, ...(errorMessage === undefined ? {} : { errorMessage }) };
            const change = { previous, current };
            const pruned = this.#prune([...snapshot.sets.at(-1)!.filter(value => value.key !== messageId).map(decodeOutbox), current]);
            const mutations: StoreMutation[] = pruned.some(value => value.key === messageId) ? [] : [{ kind: 'put', ...outboxKey(messageId), value: encodeOutbox(current) }];
            const effectSnapshot = { ...snapshot, sets: snapshot.sets.slice(0, -1) };
            const effects = this.effects?.plan(effectSnapshot, change) ?? [];
            return { mutations: [...mutations, ...pruned, ...effects], result: { change, snapshot: effectSnapshot, effects } };
        }, signal);
        if (result) {
            // Notify from the committed snapshot even when retention removed this record.
            this.changed?.(outboxStatus(result.change.current));
            this.effects?.committed?.(result.snapshot, result.effects);
        }
        return result?.change;
    }
}
function sameRequest(a: OutboxRecord, b: OutboxRecord): boolean { return messageSendRequestCodec.stringify(a.request) === messageSendRequestCodec.stringify(b.request); }
function ordinal(a: string, b: string): number { return a < b ? -1 : a > b ? 1 : 0; }
