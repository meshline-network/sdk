import { decryptMessage, type MessageDecryptor } from '../crypto/messages.js';
import { ProtocolError } from '../errors.js';
import { certificateId, type DeviceCertificate } from '../models/identity.js';
import { directMessageCodec, messageTimelinePageCodec, validateDirectMessage, validateMessageTimelinePage, type MessageTimelineEntry } from '../models/messages.js';
import type { NetworkContext } from '../protocol/context.js';
import type { JsonObject, JsonValue } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { throwIfAborted } from '../runtime/clock.js';
import { MessageRepository, type MessageEffects, type MessageInfo } from './repository.js';

export interface MessageReceptionPolicy {
    /** Performs current authority checks and prepares a synchronous transaction effect. No mutations before commit. */
    prepare(entry: MessageTimelineEntry, payload: JsonObject, signal?: AbortSignal): Promise<MessageEffects | undefined>;
    /** Only permanent business validation failures qualify. Network, key access and storage failures must remain retryable. */
    isPermanentRejection(error: unknown): error is ProtocolError;
}
export interface ReceptionResult { readonly messages: readonly MessageInfo[]; readonly rejected: readonly { readonly sequence: number; readonly error: ProtocolError }[]; readonly inserted: number }
export interface TimelineSource { read(after: number, signal?: AbortSignal): Promise<JsonValue> }
const permanentCryptographyCodes = new Set(['invalid_signature', 'invalid_identity', 'invalid_length', 'invalid_ciphertext', 'invalid_key', 'invalid_type',
    'invalid_size', 'invalid_field', 'missing_field', 'invalid_null', 'unsupported_algorithm', 'invalid_identifier', 'invalid_account', 'invalid_json',
    'duplicate_key', 'invalid_integer', 'invalid_unicode', 'invalid_utf8', 'invalid_object', 'invalid_encoding', 'invalid_base64url', 'invalid_context', 'invalid_model', 'conflicting_extension', 'invalid_number', 'invalid_depth']);

/** A serial receiver keeps the first transiently failing entry ahead of the persisted cursor. */
export class MessageReceiver {
    readonly #gate = new AsyncGate();
    constructor(readonly repository: MessageRepository, readonly context: NetworkContext, readonly device: MessageDecryptor, readonly policy: MessageReceptionPolicy) {}
    /** Results are emitted after every committed page; callbacks must not block on stopping the owning runtime. */
    synchronize(relayId: string, source: TimelineSource, committed: (result: ReceptionResult) => void, signal?: AbortSignal, observedGap?: () => void): Promise<void> {
        return this.#gate.run(async () => {
            while (true) {
                throwIfAborted(signal);
                const after = (await this.repository.getProgress(relayId, signal)).sequence;
                const page = messageTimelinePageCodec.decode(await source.read(after, signal)); validateMessageTimelinePage(page, this.context, after);
                const certificates = new Map(page.certificates.map(value => [certificateId(value, this.context), value]));
                const messages: MessageInfo[] = []; const rejected: { sequence: number; error: ProtocolError }[] = []; let inserted = 0;
                try {
                    for (const entry of page.items) {
                        const result = await this.#receive(relayId, entry, certificates.get(entry.envelope.fromDeviceId)!, page.hasRetentionGap === true, signal);
                        if (page.hasRetentionGap === true) observedGap?.();
                        if (result.error) rejected.push({ sequence: entry.sequence, error: result.error });
                        if (result.inserted) { inserted++; if (result.message) messages.push(result.message); }
                    }
                    if (!page.items.length) {
                        await this.repository.acceptEmptyPage(relayId, page.hasRetentionGap === true, signal);
                        if (page.hasRetentionGap === true) observedGap?.();
                    }
                } finally {
                    // Earlier entries stay committed if a later entry fails, and their notifications must not be lost.
                    if (inserted || rejected.length) committed({ messages, rejected, inserted });
                }
                if (!page.hasMore) return;
            }
        }, signal);
    }
    async #receive(relayId: string, entry: MessageTimelineEntry, sender: DeviceCertificate, hasRetentionGap: boolean, signal?: AbortSignal): Promise<{ inserted: boolean; error?: ProtocolError; message?: MessageInfo }> {
        if (await this.repository.acceptKnown(relayId, entry, hasRetentionGap, signal)) return { inserted: false };
        let payload: JsonObject;
        try { payload = await decryptMessage({ context: this.context, receiver: this.device, envelope: entry.envelope, keyBox: entry.keyBox, sender, ...(signal ? { signal } : {}) }); }
        catch (error) {
            throwIfAborted(signal);
            if (!(error instanceof ProtocolError) || !permanentCryptographyCodes.has(error.code)) throw error;
            await this.repository.reject(relayId, entry.sequence, error, hasRetentionGap, signal); return { inserted: false, error };
        }
        // The plaintext direct-message schema is deterministic; its rejection does not depend on local key/storage availability.
        if (payload['$type'] === 'meshline.message.direct') {
            try { validateDirectMessage(directMessageCodec.decode(payload)); }
            catch (error) {
                if (!(error instanceof ProtocolError)) throw error;
                await this.repository.reject(relayId, entry.sequence, error, hasRetentionGap, signal); return { inserted: false, error };
            }
        }
        let effects: MessageEffects | undefined;
        try { effects = await this.policy.prepare(entry, payload, signal); }
        catch (error) { return this.#rejectBusiness(relayId, entry, error, hasRetentionGap, signal); }
        // Protection failures are local infrastructure failures, even when the protector throws a ProtocolError.
        const message = await this.repository.prepare(entry.envelope, payload, signal);
        try {
            const result = await this.repository.accept(relayId, entry, message, hasRetentionGap, effects, signal);
            return { ...result, ...(result.inserted && message.payloadType === 'meshline.message.direct' ? { message: {
                ...directMessageCodec.decode(payload), localSequence: result.localSequence, key: { sender: message.sender, messageId: message.messageId }, senderDeviceId: message.senderDeviceId, recipient: message.recipient, createdAt: message.createdAt,
            } } : {}) };
        }
        catch (error) { return this.#rejectBusiness(relayId, entry, error, hasRetentionGap, signal); }
    }
    async #rejectBusiness(relayId: string, entry: MessageTimelineEntry, error: unknown, hasRetentionGap: boolean, signal?: AbortSignal): Promise<{ inserted: false; error: ProtocolError }> {
        throwIfAborted(signal);
        if (!this.policy.isPermanentRejection(error)) throw error;
        await this.repository.reject(relayId, entry.sequence, error, hasRetentionGap, signal); return { inserted: false, error };
    }
}
