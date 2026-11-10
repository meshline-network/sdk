import { backupRows, snapshotRecords } from '../backups/snapshot.js';
import { boolean, failure, fields, includes, integer, record, text, type Outcome } from '../backups/data.js';
import type { BackupSelection } from '../models/backup.js';
import { validateAccountId, validateRelayId } from '../identity/neo.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { groupEventCodec, groupManagementPayloadCodec, verifyGroupManagementPayload, type GroupEvent } from '../models/group-management.js';
import { groupMessageCodec, groupMessageEnvelopeCodec, groupMemberNicknameUpdateCodec, validateGroupPayload, verifyGroupMessageEnvelope, type GroupRef } from '../models/groups.js';
import { certificateId, deviceCertificateCodec, validateCertificate, type DeviceCertificate } from '../models/identity.js';
import { canonicalJson, parseJson, requireObject, type JsonObject } from '../protocol/json.js';
import type { NetworkContext } from '../protocol/context.js';
import type { MeshlineStore, StoreReadSnapshot, StoreMutation } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { decodeGroupProjection, encodeGroupProjection, groupEventKey, groupKey, groupMessageIdentity, groupPendingMessageKey, type GroupPageResult, type GroupRepository } from './repository.js';
import { groupSecretBackupCollections, type GroupSecrets } from './secrets.js';
import type { GroupMessageInfo } from './messages.js';

const groupBackupCollections = ['groups', 'group_events', ...groupSecretBackupCollections];
const recordFields: Readonly<Record<string, string>> = {
    group: 'group_id relay_id locally_closed',
    group_event: 'group_id sequence epoch payload_json certificate_json message_id sender sender_device_id created_at decrypted_payload_json rejection is_message',
    group_nickname: 'group_id account_id joined_at_sequence sequence nickname',
    group_epoch: 'group_id epoch commitment member_public_key client_secret application_secret',
    group_secret: 'group_id kind id secret',
};
export interface GroupBackupChange { readonly outcome: Outcome; readonly group?: GroupRef; readonly change?: GroupPageResult['change']; readonly message?: GroupMessageInfo; readonly block?: boolean; readonly confirmed?: readonly string[] }
const document = (value: JsonObject[string] | undefined): JsonObject | undefined => value == null ? undefined : requireObject(parseJson(text(value)));
function messageInfo(group: GroupRef, value: JsonObject): GroupMessageInfo | undefined {
    if (!value.isMessage) return undefined;
    const event = groupEventCodec.decode(value.event!); const message = requireObject(value.message!);
    return { ...groupMessageCodec.decode(value.decryptedPayload!), group, sequence: event.sequence, localSequence: event.sequence, sender: text(message.sender), senderDeviceId: text(message.senderDeviceId), messageId: text(message.messageId), createdAt: integer(message.createdAt), acceptedAt: event.acceptedAt };
}
export function exportGroups(store: MeshlineStore, secrets: GroupSecrets, history: BackupSelection, signal: AbortSignal): AsyncGenerator<JsonObject> {
    return snapshotRecords(store, groupBackupCollections, snapshot => groupRecords(snapshot, secrets, history, signal), signal);
}
async function* groupRecords(snapshot: StoreReadSnapshot, secrets: GroupSecrets, history: BackupSelection, signal: AbortSignal): AsyncGenerator<JsonObject> {
    for await (const row of backupRows(snapshot, { collection: 'groups' }, signal)) yield record('group', { group_id: row.key, relay_id: row.value.relayId!, locally_closed: row.value.locallyClosed === true });
    for await (const row of backupRows(snapshot, { collection: 'group_events' }, signal)) {
        const groupId = row.key.split('|')[0]!; const value = row.value; const event = groupEventCodec.decode(value.event!);
        if (event.payload.$type === 'meshline.group.message' && !includes(history, groupId)) continue;
        const message = value.message == null ? undefined : requireObject(value.message);
        yield record('group_event', { group_id: groupId, sequence: event.sequence, epoch: event.epoch, payload_json: canonicalJson(event.payload), certificate_json: value.certificate ? canonicalJson(value.certificate) : null,
            message_id: message?.messageId ?? null, sender: message?.sender ?? null, sender_device_id: message?.senderDeviceId ?? null, created_at: message?.createdAt == null ? null : new Date(Number(message.createdAt) * 1000).toISOString(),
            decrypted_payload_json: value.decryptedPayload ? canonicalJson(value.decryptedPayload) : null, rejection: value.rejection ? text(requireObject(value.rejection).message) : null, is_message: value.isMessage === true });
    }
    for await (const row of backupRows(snapshot, { collection: 'groups' }, signal)) {
        if (!row.value.projection) continue; const group = { groupId: row.key, relayId: text(row.value.relayId) }; const projection = decodeGroupProjection(requireObject(row.value.projection), group);
        for (const member of [...projection.members].sort((a, b) => a.account < b.account ? -1 : a.account > b.account ? 1 : 0)) if (member.nicknameSequence !== undefined)
            yield record('group_nickname', { group_id: row.key, account_id: member.account, joined_at_sequence: member.joinedAtSequence, sequence: member.nicknameSequence, nickname: member.nickname ?? null });
    }
    yield* secrets.exportBackup(snapshot, signal);
}
function eventRecord(data: JsonObject, context: NetworkContext): { value: JsonObject; event: GroupEvent; certificate?: DeviceCertificate } {
    const sequence = integer(data.sequence); const epoch = integer(data.epoch); const payload = document(data.payload_json) ?? failure('Missing group payload.');
    const certificateWire = document(data.certificate_json); const certificate = certificateWire && deviceCertificateCodec.decode(certificateWire); if (certificate) validateCertificate(certificate, context);
    const plaintext = document(data.decrypted_payload_json); const isMessage = boolean(data.is_message); const rejected = data.rejection != null; const chat = payload.$type === 'meshline.group.message';
    const event: GroupEvent = { sequence, epoch, payload, acceptedAt: 0, ...(certificate ? { signerDeviceId: certificateId(certificate, context) } : {}) };
    const value: JsonObject = { event: groupEventCodec.encode(event), ...(certificateWire ? { certificate: certificateWire } : {}), isMessage, ...(plaintext ? { decryptedPayload: plaintext } : {}) };
    if (chat) {
        const message: JsonObject = {};
        for (const [field, name] of [['sender', 'sender'], ['message_id', 'messageId'], ['sender_device_id', 'senderDeviceId']] as const)
            if (data[field] != null) message[name] = text(data[field]);
        if (data.created_at != null) {
            const created = Date.parse(text(data.created_at)); if (!Number.isFinite(created)) failure('Invalid group message creation time.');
            message.createdAt = created / 1000;
        }
        if (Object.keys(message).length) value.message = message;
    }
    if (rejected) {
        if (!chat || isMessage || plaintext) failure('Only rejected message envelopes may bypass content verification.');
        value.rejection = { code: 'invalid_message', message: text(data.rejection) };
    } else if (payload.$type === 'meshline.group.key.rotated') {
        if (certificate || isMessage || plaintext) failure('Invalid relay rotation metadata.');
    } else {
        if (!certificate) failure('Missing event signing certificate.');
        if (payload.group_id !== data.group_id) failure('Group event binding differs.');
        if (chat) {
            const envelope = groupMessageEnvelopeCodec.decode(payload); verifyGroupMessageEnvelope(envelope, certificate!, certificateId(certificate!, context), context);
            const created = data.created_at == null ? NaN : Date.parse(text(data.created_at)) / 1000;
            if (envelope.epoch !== epoch || envelope.messageId !== data.message_id || certificate!.account !== data.sender || event.signerDeviceId !== data.sender_device_id || created !== envelope.createdAt) failure('Group message metadata differs from signed evidence.');
            if (plaintext) validateGroupPayload(plaintext);
            if (isMessage !== (plaintext?.$type === 'meshline.group.message.content')) failure('Invalid group history classification.');
            value.message = { sender: data.sender!, messageId: data.message_id!, senderDeviceId: data.sender_device_id!, createdAt: created, joinedAtSequence: 0 };
        } else {
            verifyGroupManagementPayload(groupManagementPayloadCodec.decode(payload), certificate!, context);
            if (isMessage || plaintext) failure('Management events cannot contain chat history.');
        }
    }
    return { value, event, ...(certificate ? { certificate } : {}) };
}
export async function importGroup(store: MeshlineStore, repository: GroupRepository, secrets: GroupSecrets, type: string, data: JsonObject, blocked: ReadonlySet<string>, signal?: AbortSignal): Promise<GroupBackupChange> {
    fields(data, recordFields[type] ?? failure('Unsupported group record.'));
    const groupId = text(data.group_id); validateIdentifier('group', groupId);
    if (type === 'group') {
        const relayId = text(data.relay_id); validateRelayId(relayId); const locallyClosed = boolean(data.locally_closed);
        return updateStore(store, [{ collection: 'groups', key: groupId }], snapshot => {
            const current = snapshot.sets[0]![0]?.value;
            return { mutations: current ? [] : [{ kind: 'put', collection: 'groups', key: groupId, value: { relayId, locallyClosed } }], result: { outcome: !current ? 'applied' : current.relayId === relayId ? 'unchanged' : 'conflicts', ...(current && current.relayId !== relayId ? { block: true } : {}) } };
        }, signal);
    }
    if (blocked.has(groupId)) return { outcome: 'unchanged' };
    if (type === 'group_epoch' || type === 'group_secret') return { outcome: await secrets.importBackup(type, data, signal) };
    const initial = (await store.read([{ collection: 'groups', key: groupId }], signal)).sets[0]![0]?.value;
    if (!initial) failure('Group data precedes its reference.'); const group = { groupId, relayId: text(initial!.relayId) };
    if (type === 'group_nickname') {
        const account = text(data.account_id); validateAccountId(account); const joined = integer(data.joined_at_sequence); const sequence = integer(data.sequence, joined);
        const nickname = data.nickname == null ? null : text(data.nickname); validateGroupPayload(groupMemberNicknameUpdateCodec.encode({ nickname }));
        return updateStore<GroupBackupChange>(store, [groupKey(group)], snapshot => {
            const current = snapshot.sets[0]![0]!.value; const projection = current.projection == null ? undefined : decodeGroupProjection(requireObject(current.projection), group); const member = projection?.members.find(item => item.account === account);
            if (!member || member.joinedAtSequence !== joined || (member.nicknameSequence ?? -1) > sequence) return { mutations: [], result: { outcome: 'unchanged' } };
            if (member.nicknameSequence === sequence) return { mutations: [], result: { outcome: (member.nickname ?? null) === nickname ? 'unchanged' : 'conflicts' } };
            const nextMember = { ...member, nicknameSequence: sequence }; if (nickname === null) delete nextMember.nickname; else nextMember.nickname = nickname;
            const next = { ...current, projection: encodeGroupProjection({ ...projection!, members: projection!.members.map(item => item === member ? nextMember : item) }) };
            return { mutations: [{ kind: 'put', ...groupKey(group), value: next }], result: { outcome: 'applied', group, change: { record: next, kinds: ['nickname'] } } };
        }, signal);
    }
    const incoming = eventRecord(data, repository.context); const projection = initial!.projection == null ? undefined : decodeGroupProjection(requireObject(initial!.projection), group); const key = groupEventKey(group, incoming.event.sequence);
    const current = (await store.read([key], signal)).sets[0]![0]?.value;
    if (current) {
        const event = groupEventCodec.decode(current.event!);
        if (canonicalJson(event.payload) !== canonicalJson(incoming.event.payload) || event.epoch !== incoming.event.epoch || current.decryptedPayload && incoming.value.decryptedPayload && canonicalJson(current.decryptedPayload) !== canonicalJson(incoming.value.decryptedPayload)) return { outcome: 'conflicts', block: true };
    }
    if (incoming.event.sequence <= (projection?.sequence ?? -1)) {
        if (!current && incoming.event.payload.$type !== 'meshline.group.message') return { outcome: 'conflicts', block: true };
        if (current && (current.rejection || current.decryptedPayload || !incoming.value.decryptedPayload)) return { outcome: 'unchanged' };
        const next = current ? { ...current, decryptedPayload: incoming.value.decryptedPayload!, isMessage: incoming.value.isMessage! } : incoming.value;
        const identity = groupMessageIdentity(group, next.message == null ? undefined : requireObject(next.message));
        await updateStore(store, [key, ...(identity ? [identity] : [])], snapshot => {
            if (canonicalJson(snapshot.sets[0]![0]?.value ?? null) !== canonicalJson(current ?? null)) failure('Group event changed during import.');
            const mutations: StoreMutation[] = [{ kind: 'put', ...key, value: next }];
            if (identity && !snapshot.sets[1]!.length) mutations.push({ kind: 'put', ...identity, value: { sequence: incoming.event.sequence } });
            if (next.decryptedPayload) mutations.push({ kind: 'delete', ...groupPendingMessageKey(group, incoming.event.sequence) });
            else if (!next.rejection) mutations.push({ kind: 'put', ...groupPendingMessageKey(group, incoming.event.sequence), value: { sequence: incoming.event.sequence } });
            return { mutations, result: undefined };
        }, signal);
        const restored = messageInfo(group, next); return { outcome: 'applied', group, ...(restored ? { message: restored } : {}) };
    }
    const applied = await repository.applyPage(group, { events: [incoming.event], certificates: incoming.certificate ? [incoming.certificate] : [], hasMore: false }, initial, signal, incoming.value);
    const saved = await repository.event(group, incoming.event.sequence, signal); const message = saved && messageInfo(group, saved);
    return { outcome: 'applied', group, ...(applied.change ? { change: applied.change } : {}), ...(applied.confirmed ? { confirmed: applied.confirmed } : {}), ...(message ? { message } : {}) };
}
