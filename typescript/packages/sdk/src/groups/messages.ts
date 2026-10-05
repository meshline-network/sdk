import { decryptGroupMessage, GroupKeyAccessError } from '../crypto/groups.js';
import { requireLength } from '../crypto/primitives.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import { groupEventCodec } from '../models/group-management.js';
import { groupMessageCodec, groupMessageEnvelopeCodec, groupMemberNicknameUpdateCodec, validateGroupPayload, validateGroupRef, type GroupMessage, type GroupRef } from '../models/groups.js';
import { deviceCertificateCodec } from '../models/identity.js';
import type { NetworkContext } from '../protocol/context.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { throwIfAborted } from '../runtime/clock.js';
import type { MeshlineStore, StoreMutation } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { decodeGroupProjection, encodeGroupProjection, groupEpochKey, groupEventKey, groupKey, groupPendingMessageKey } from './repository.js';
import type { GroupProjection } from './state.js';

export interface GroupMessageInfo extends GroupMessage {
    readonly group: GroupRef; readonly sequence: number; readonly messageId: string; readonly sender: string; readonly senderDeviceId: string; readonly createdAt: number; readonly acceptedAt: number;
}
export interface GroupMessageProcessingResult { readonly state: 'waitingForKey' | 'processed' | 'alreadyProcessed'; readonly requiresKey?: boolean; readonly message?: GroupMessageInfo; readonly nicknameChanged?: boolean; readonly groupRecord?: JsonObject; readonly error?: ProtocolError }
export interface GroupApplicationSecrets {
    /** Returns a fresh owned buffer only after the epoch's authority is verified. Caller erases it. */
    readApplicationSecret(group: GroupRef, epoch: number, signal?: AbortSignal): Promise<Uint8Array | undefined>;
}
/** Sequence is scoped to this sender's and this recipient's current membership; unknown payload attributes never select the target member. */
export function applyGroupNickname(projection: GroupProjection, accountId: string, sender: string, sequence: number, nickname: string | null): GroupProjection {
    requireSafeInteger(sequence, 1);
    const receiver = projection.members.find(member => member.account === accountId); const member = projection.members.find(member => member.account === sender);
    if (!receiver || !member || sequence < receiver.joinedAtSequence || sequence < member.joinedAtSequence || sequence <= (member.nicknameSequence ?? -1)) return projection;
    const next = { ...member, nicknameSequence: sequence }; if (nickname === null) delete next.nickname; else next.nickname = nickname;
    return { ...projection, members: projection.members.map(value => value.account === sender ? next : value) };
}

/** Decryption is separate from verified timeline ingestion, so a missing old key cannot prevent recovery of later epochs. */
export class GroupMessageProcessor {
    constructor(readonly store: MeshlineStore, readonly context: NetworkContext, readonly accountId: string, readonly secrets: GroupApplicationSecrets) {}
    async processPending(group: GroupRef, committed: (sequence: number, result: GroupMessageProcessingResult) => void, signal?: AbortSignal): Promise<boolean> {
        let pending = false;
        validateGroupRef(group); const reader = await this.store.openQuery({ collection: 'group_pending_messages', prefix: `${group.groupId}|` }, signal);
        try {
            while (true) {
                const rows = await reader.readNext(128, signal); if (!rows.length) break;
                for (const row of rows) { requireSafeInteger(row.value.sequence, 1); const result = await this.process(group, row.value.sequence, signal); if (result.state === 'processed') committed(row.value.sequence, result);
                    if (result.requiresKey) pending = true; }
            }
        } finally { await reader.dispose(); }
        return pending;
    }
    async process(group: GroupRef, sequence: number, signal?: AbortSignal): Promise<GroupMessageProcessingResult> {
        validateGroupRef(group); requireSafeInteger(sequence, 1); throwIfAborted(signal);
        const queries = [groupKey(group), groupEventKey(group, sequence), groupPendingMessageKey(group, sequence)];
        const initial = await this.store.read(queries, signal); const groupRow = initial.sets[0]![0]?.value; const stored = initial.sets[1]![0]?.value;
        if (groupRow?.relayId !== group.relayId || !stored) throw new ProtocolError('invalid_storage', 'Verified group message or its hosting association is missing.');
        const projection = decodeGroupProjection(requireObject(groupRow.projection!), group);
        if (stored.decryptedPayload !== undefined || stored.rejection !== undefined || !initial.sets[2]!.length) return { state: 'alreadyProcessed' };
        const event = groupEventCodec.decode(stored.event!); const message = requireObject(stored.message!); const sender = deviceCertificateCodec.decode(stored.certificate!);
        if (event.sequence !== sequence || projection.sequence < sequence || message.sender !== sender.account || message.senderDeviceId !== event.signerDeviceId) throw new ProtocolError('invalid_storage', 'Stored group message evidence has inconsistent identities or sequence.');
        const envelope = groupMessageEnvelopeCodec.decode(event.payload);
        if (message.messageId !== envelope.messageId || message.createdAt !== envelope.createdAt || envelope.groupId !== group.groupId || envelope.epoch !== event.epoch) throw new ProtocolError('invalid_storage', 'Stored group envelope differs from its verified metadata.');
        const epoch = (await this.store.read([groupEpochKey(group, event.epoch)], signal)).sets[0]![0];
        if (!epoch?.value.commitment) throw new ProtocolError('invalid_storage', 'Verified group message has no established epoch.');
        // Key access is outside the permanent-content rejection boundary, even when a local provider throws ProtocolError.
        const secret = await this.secrets.readApplicationSecret(group, event.epoch, signal); if (!secret) return { state: 'waitingForKey', requiresKey: epoch.value.memberPublicKey !== undefined };
        let payload: JsonObject | undefined; let error: ProtocolError | undefined;
        try {
            throwIfAborted(signal); try { requireLength(secret, 32, 'Stored application secret'); } catch (cause) { throw new GroupKeyAccessError('Local group application secret has an invalid length.', { cause }); }
            try {
                payload = decryptGroupMessage({ context: this.context, envelope, sender, signerDeviceId: event.signerDeviceId!, applicationSecret: secret }); validateGroupPayload(payload);
                if (payload.$type === 'meshline.group.message.content' && (groupMessageCodec.decode(payload).replyToSeq ?? 0) >= sequence) throw new ProtocolError('invalid_reply', 'Group reply sequence must precede its containing message.');
            } catch (cause) { if (!(cause instanceof ProtocolError)) throw cause; error = cause; }
        } finally { secret.fill(0); }
        throwIfAborted(signal);
        return updateStore(this.store, queries, snapshot => {
            const current = snapshot.sets[1]![0]?.value; const currentGroup = snapshot.sets[0]![0]?.value;
            if (current?.decryptedPayload !== undefined || current?.rejection !== undefined) return { mutations: [], result: { state: 'alreadyProcessed' } as GroupMessageProcessingResult };
            if (canonicalJson(current ?? null) !== canonicalJson(stored) || currentGroup?.relayId !== group.relayId) throw new StateConflictError('Group message evidence changed during decryption.');
            const mutations: StoreMutation[] = [{ kind: 'delete', ...groupPendingMessageKey(group, sequence) }];
            const result: GroupMessageProcessingResult = error ? { state: 'processed', error } : { state: 'processed' };
            if (error) mutations.push({ kind: 'put', ...groupEventKey(group, sequence), value: { ...current, rejection: { code: error.code, message: error.message } } });
            else {
                const content = payload!; let next = decodeGroupProjection(requireObject(currentGroup.projection!), group); const before = next;
                if (content.$type === 'meshline.group.member.nickname.update') next = applyGroupNickname(next, this.accountId, sender.account, sequence, groupMemberNicknameUpdateCodec.decode(content).nickname);
                const groupRecord = next === before ? undefined : { ...currentGroup, projection: encodeGroupProjection(next) };
                if (groupRecord) mutations.push({ kind: 'put', ...groupKey(group), value: groupRecord });
                const isMessage = content.$type === 'meshline.group.message.content';
                mutations.push({ kind: 'put', ...groupEventKey(group, sequence), value: { ...current, decryptedPayload: content, isMessage } });
                return { mutations, result: { state: 'processed', ...(groupRecord ? { nicknameChanged: true, groupRecord } : {}), ...(isMessage ? { message: {
                    ...groupMessageCodec.decode(content), group: { ...group }, sequence, sender: sender.account, senderDeviceId: event.signerDeviceId!, messageId: envelope.messageId, createdAt: envelope.createdAt, acceptedAt: event.acceptedAt,
                } } : {}) } as GroupMessageProcessingResult };
            }
            return { mutations, result };
        }, signal);
    }
}
