import type { DeviceManager } from '../components/device.js';
import type { MessageManager } from '../components/message.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import { sendGroupAccountPayload } from '../messages/account-sender.js';
import { cachedDeviceStateKey } from '../messages/contacts.js';
import type { AccountMessage, MessageEffects } from '../messages/repository.js';
import { accountGroupHistorySecretSyncCodec, accountGroupPrivateStateRequestCodec, accountGroupPrivateStateSyncCodec,
    validateAccountGroupHistorySecretSync, validateAccountGroupPrivateStateRequest, validateAccountGroupPrivateStateSync, type GroupHistorySecret, type GroupRef } from '../models/groups.js';
import { accountDeviceStateCodec, authorizedDevice, certificateId } from '../models/identity.js';
import type { NetworkContext } from '../protocol/context.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import type { MeshlineStore, RecordKey, RecordQuery, StoreMutation, StoreSnapshot } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { decodeGroupProjection, groupEpochKey, groupKey, GroupRepository } from './repository.js';
import { groupApplicationSecretKey, groupMemberSecretKey, type GroupSecrets, type GroupSecretEffect } from './secrets.js';

export interface GroupAccountSyncOptions {
    readonly store: MeshlineStore; readonly context: NetworkContext; readonly accountId: string; readonly clock: RuntimeClock;
    readonly device: DeviceManager; readonly messages: MessageManager; readonly secrets: GroupSecrets;
    rejected(messageId: string, error: ProtocolError): void;
}
const cursorKey: RecordKey = { collection: 'group_account_meta', key: 'local_sequence' };
const types = new Set(['meshline.account.group.state.request', 'meshline.account.group.state.sync', 'meshline.account.group.history_secret.sync']);
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
class AccountGroupRejection extends ProtocolError {}
function input<T>(action: () => T): T {
    try { return action(); } catch (error) { if (!(error instanceof ProtocolError)) throw error; throw new AccountGroupRejection(error.code, error.message, { cause: error }); }
}
function secretEffect(effect: GroupSecretEffect): GroupSecretEffect {
    return { queries: effect.queries, plan: snapshot => {
        try { return effect.plan(snapshot); }
        catch (error) { if (!(error instanceof ProtocolError) || error.code !== 'invalid_binding') throw error; throw new AccountGroupRejection(error.code, error.message, { cause: error }); }
    } };
}

/** One consumer cursor commits with all key effects; local protection/storage failures remain retryable. */
export class GroupAccountSync {
    constructor(readonly options: GroupAccountSyncOptions) {}
    request(signal?: AbortSignal): Promise<void> { return sendGroupAccountPayload(this.options.messages, accountGroupPrivateStateRequestCodec.encode({}), undefined, signal); }
    async consume(signal: AbortSignal): Promise<void> {
        const { store, messages } = this.options;
        for (;;) {
            const after = (await store.read([cursorKey], signal)).sets[0]![0]?.value.sequence ?? 0; requireSafeInteger(after, 0);
            const page = await messages.readTimeline(after, 64, signal); if (!page.length) return;
            for (const message of page) {
                throwIfAborted(signal); let effect: GroupSecretEffect | undefined; let rejection: AccountGroupRejection | undefined;
                try { effect = await this.#prepare(message, signal); }
                catch (error) { throwIfAborted(signal); if (!(error instanceof AccountGroupRejection)) throw error; rejection = error; }
                try { await this.#commit(message, effect, rejection, signal); }
                catch (error) {
                    throwIfAborted(signal); if (rejection || !(error instanceof AccountGroupRejection)) throw error;
                    rejection = error; await this.#commit(message, undefined, rejection, signal);
                }
                if (rejection) this.options.rejected(message.messageId, rejection);
            }
        }
    }
    async #commit(message: AccountMessage, effect: GroupSecretEffect | undefined, rejection: ProtocolError | undefined, signal: AbortSignal): Promise<void> {
        requireSafeInteger(message.localSequence, 1);
        await updateStore(this.options.store, [cursorKey, ...effect?.queries ?? []], snapshot => {
            const previous = snapshot.sets[0]![0]?.value.sequence ?? 0; requireSafeInteger(previous, 0);
            if (previous >= message.localSequence) return { mutations: [], result: undefined };
            const mutations: StoreMutation[] = [...effect?.plan({ ...snapshot, sets: snapshot.sets.slice(1) }) ?? [], { kind: 'put', ...cursorKey, value: { sequence: message.localSequence } }];
            if (rejection) mutations.push({ kind: 'put', collection: 'group_account_rejections', key: String(message.localSequence).padStart(16, '0'), value: { messageId: message.messageId, code: rejection.code, message: rejection.message } });
            return { mutations, result: undefined };
        }, signal);
    }
    #authorize(senderDeviceId: string, effect?: GroupSecretEffect): GroupSecretEffect {
        return { queries: [cachedDeviceStateKey(this.options.accountId), ...effect?.queries ?? []], plan: snapshot => {
            const row = snapshot.sets[0]![0]?.value; if (!row) throw new StateConflictError('Current device authorization disappeared during group account synchronization.');
            const state = accountDeviceStateCodec.decode(row); if (state.account !== this.options.accountId) throw new ProtocolError('invalid_storage', 'Cached device authorization belongs to another account.');
            input(() => authorizedDevice(state, senderDeviceId, this.options.context, this.options.clock.nowSeconds()));
            return effect?.plan({ ...snapshot, sets: snapshot.sets.slice(1) }) ?? [];
        } };
    }
    async #prepare(message: AccountMessage, signal: AbortSignal): Promise<GroupSecretEffect | undefined> {
        if (!types.has(String(message.payload['$type']))) return undefined;
        const { accountId, device, context, secrets } = this.options;
        if (message.sender !== accountId || message.recipient !== accountId) throw new AccountGroupRejection('invalid_binding', 'Private group synchronization must stay within the current account.');
        const own = await device.getDeviceState(undefined, signal); if (!own) throw new ProtocolError('device_state_required', 'Current account authorization is unavailable.');
        input(() => authorizedDevice(own, message.senderDeviceId, context, this.options.clock.nowSeconds()));
        let effect: GroupSecretEffect | undefined;
        if (message.payload['$type'] === 'meshline.account.group.state.request') {
            const request = input(() => { const value = accountGroupPrivateStateRequestCodec.decode(message.payload); validateAccountGroupPrivateStateRequest(value); return value; });
            if (message.senderDeviceId !== certificateId(device.certificate, context)) await this.#reply(message.senderDeviceId, request.groupId, signal);
        } else if (message.payload['$type'] === 'meshline.account.group.state.sync') {
            const value = input(() => accountGroupPrivateStateSyncCodec.decode(message.payload));
            try { input(() => validateAccountGroupPrivateStateSync(value)); effect = secretEffect(await secrets.prepareMemberStates(value.states, signal)); }
            finally { for (const state of value.states) state.memberEncryptionPrivateKey.fill(0); }
        } else {
            const value = input(() => accountGroupHistorySecretSyncCodec.decode(message.payload));
            try {
                input(() => validateAccountGroupHistorySecretSync(value));
                try { effect = await secrets.prepareHistorySecrets(value.secrets, signal); }
                catch (error) { if (!(error instanceof ProtocolError) || error.code !== 'conflicting_epoch_secret') throw error; throw new AccountGroupRejection(error.code, error.message, { cause: error }); }
            } finally { for (const secret of value.secrets) secret.applicationSecret.fill(0); }
        }
        return this.#authorize(message.senderDeviceId, effect);
    }
    #member(group: GroupRef, row: JsonObject | undefined) {
        if (!row) return undefined; if (row.relayId !== group.relayId) throw new ProtocolError('invalid_binding', 'Synchronized group belongs to another hosting relay.');
        if (!row.projection) return undefined; const projection = decodeGroupProjection(requireObject(row.projection), group);
        if (row.localDepartureAfter !== undefined) { requireSafeInteger(row.localDepartureAfter, 0); if (projection.sequence <= row.localDepartureAfter) return undefined; }
        return projection.members.some(value => value.account === this.options.accountId) ? projection : undefined;
    }
    #guard(queries: readonly RecordQuery[], before: StoreSnapshot, mutations: readonly StoreMutation[] = []): MessageEffects {
        return { queries, plan: snapshot => {
            if (snapshot.sets.some((rows, index) => fingerprint(rows[0]?.value) !== fingerprint(before.sets[index]![0]?.value))) throw new StateConflictError('Group key authority changed before account synchronization was enqueued.');
            return mutations;
        } };
    }
    async shareCurrent(group: GroupRef, signal: AbortSignal): Promise<void> { await this.#sendPrivate(group, undefined, signal); }
    async #sendPrivate(group: GroupRef, devices: readonly string[] | undefined, signal: AbortSignal): Promise<void> {
        const { store, secrets, messages } = this.options; const row = (await store.read([groupKey(group)], signal)).sets[0]![0]?.value;
        const projection = this.#member(group, row); if (!projection) return;
        const member = projection.members.find(value => value.account === this.options.accountId)!;
        const key = groupMemberSecretKey(group.groupId, member.memberEncryptionPublicKey); const queries = [groupKey(group), groupEpochKey(group, projection.epoch), key]; const snapshot = await store.read(queries, signal);
        if (fingerprint(snapshot.sets[0]![0]?.value) !== fingerprint(row)) throw new StateConflictError('Group authority changed before key sharing.');
        const stored = snapshot.sets[2]![0]?.value; if (!stored || devices === undefined && stored.shared === true || snapshot.sets[1]![0]?.value.materialVerified !== true) return;
        const privateKey = await secrets.readMemberKey(group.groupId, member.memberEncryptionPublicKey, signal); if (!privateKey) return;
        try {
            await sendGroupAccountPayload(messages, accountGroupPrivateStateSyncCodec.encode({ states: [{ ...group, memberEncryptionPrivateKey: privateKey }] }), devices, signal,
                this.#guard(queries, snapshot, devices === undefined ? [{ kind: 'put', ...key, value: { ...stored, shared: true } }] : []));
        } finally { privateKey.fill(0); }
    }
    async #reply(sender: string, requested: string | undefined, signal: AbortSignal): Promise<void> {
        const { store, context, accountId, secrets, messages } = this.options; const repository = new GroupRepository(store, context, accountId);
        const rows = (await store.read([{ collection: 'groups', ...(requested ? { key: requested } : {}) }], signal)).sets[0]!;
        for (const row of rows) {
            const group = { groupId: row.key, relayId: String(row.value.relayId) }; if (!this.#member(group, row.value)) continue;
            await this.#sendPrivate(group, [sender], signal); const epochs = (await repository.epochs(group, signal)).filter(value => value.commitment);
            for (let start = 0; start < epochs.length; start += 64) {
                const batch = epochs.slice(start, start + 64); const queries = [groupKey(group), ...batch.flatMap(value => [groupEpochKey(group, value.epoch), groupApplicationSecretKey(group.groupId, value.epoch)])];
                const snapshot = await store.read(queries, signal); if (!this.#member(group, snapshot.sets[0]![0]?.value)) break;
                const history: GroupHistorySecret[] = [];
                try {
                    for (const value of batch) { const secret = await secrets.readApplicationSecret(group, value.epoch, signal); if (secret) history.push({ groupId: group.groupId, epoch: value.epoch, applicationSecret: secret }); }
                    if (history.length) await sendGroupAccountPayload(messages, accountGroupHistorySecretSyncCodec.encode({ secrets: history }), [sender], signal, this.#guard(queries, snapshot));
                } finally { for (const value of history) value.applicationSecret.fill(0); }
            }
        }
    }
}
