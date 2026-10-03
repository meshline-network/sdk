import { deriveGroupApplicationSecret, groupClientSecretBoxAad, groupClientSecretCommitment, groupRelaySecretBoxAad, GroupKeyAccessError, openGroupSecret } from '../crypto/groups.js';
import { agreeKey, encryptionPublicKey, requireLength } from '../crypto/primitives.js';
import type { MessageDecryptor } from '../crypto/messages.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import type { SecretProtector } from '../interactions.js';
import { contentHashBytes } from '../models/content.js';
import { groupKeyEntryCodec, validateGroupKeyEntry } from '../models/group-keys.js';
import { groupHistorySecretCodec, groupMemberPrivateStateCodec, validateAccountGroupHistorySecretSync, validateAccountGroupPrivateStateSync, validateGroupMemberPrivateState, validateGroupRef, type GroupHistorySecret, type GroupMemberPrivateState, type GroupRef } from '../models/groups.js';
import { certificateId, deviceCertificateCodec, validateCertificate } from '../models/identity.js';
import type { NetworkContext } from '../protocol/context.js';
import { decodeBase64Url, encodeBase64Url, equalBytes } from '../protocol/encoding.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { throwIfAborted } from '../runtime/clock.js';
import type { MeshlineStore, RecordKey, RecordQuery, StoreMutation, StoreSnapshot } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import { decodeGroupProjection, groupEpochKey, groupKey, GroupRepository } from './repository.js';
import type { GroupApplicationSecrets } from './messages.js';

const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
const put = (key: RecordKey, value: JsonObject): StoreMutation => ({ ...key, kind: 'put', value });
export const groupMemberSecretKey = (groupId: string, publicKey: Uint8Array): RecordKey => ({ collection: 'group_member_keys', key: `${groupId}|${encodeBase64Url(publicKey)}` });
export const groupApplicationSecretKey = (groupId: string, epoch: number): RecordKey => ({ collection: 'group_application_secrets', key: `${groupId}|${String(epoch).padStart(16, '0')}` });
export const groupRotationKey = (group: GroupRef): RecordKey => ({ collection: 'group_rotations', key: group.groupId });
const clientSecretKey = (groupId: string, commitment: string): RecordKey => ({ collection: 'group_client_secrets', key: `${groupId}|${commitment}` });
export interface PreparedGroupMemberKey { readonly group: GroupRef; readonly publicKey: Uint8Array; readonly protectedKey: string; readonly deviceId: string }
export interface GroupSecretEffect { readonly queries: readonly RecordQuery[]; plan(snapshot: StoreSnapshot): readonly StoreMutation[] }
export interface GroupSecretsOptions { readonly store: MeshlineStore; readonly context: NetworkContext; readonly accountId: string; readonly deviceId: () => string; readonly protector: SecretProtector }
export interface GroupKeyRecoveryResult { readonly derived: readonly number[]; readonly missing: readonly number[]; readonly rejected: readonly { readonly epoch: number; readonly error: ProtocolError }[] }
export interface GroupRotation { readonly group: GroupRef; readonly baseCommitment: string; readonly commitment: string; readonly ownerSequence: number; readonly deviceId: string; readonly ownerPublicKey?: Uint8Array; readonly expiresAt?: number }
function rotationRecord(value: JsonObject, group: GroupRef): GroupRotation {
    validateGroupRef(group);
    if (value.relayId !== group.relayId || typeof value.baseCommitment !== 'string' || typeof value.commitment !== 'string' || typeof value.deviceId !== 'string' || typeof value.protectedSecret !== 'string') throw new ProtocolError('invalid_storage', 'Stored rotation has inconsistent group, commitment or secret metadata.');
    contentHashBytes(value.baseCommitment); contentHashBytes(value.commitment); validateIdentifier('device', value.deviceId); requireSafeInteger(value.ownerSequence, 0);
    if (value.baseCommitment === value.commitment) throw new ProtocolError('invalid_storage', 'Stored rotation did not select a new secret.');
    if (value.expiresAt !== undefined) requireSafeInteger(value.expiresAt, 1);
    const ownerPublicKey = value.ownerPublicKey === undefined ? undefined : decodeBase64Url(String(value.ownerPublicKey)); if (ownerPublicKey) requireLength(ownerPublicKey, 32, 'Candidate owner public key');
    return { group, baseCommitment: value.baseCommitment, commitment: value.commitment, ownerSequence: value.ownerSequence, deviceId: value.deviceId, ...(ownerPublicKey ? { ownerPublicKey } : {}), ...(value.expiresAt === undefined ? {} : { expiresAt: value.expiresAt }) };
}

/** Account-message authentication is the caller's responsibility; arriving keys never select current membership or advance verified history. */
export class GroupSecrets implements GroupApplicationSecrets {
    constructor(readonly options: GroupSecretsOptions) { validateAccountId(options.accountId); }
    #purpose(groupId: string, kind: string, key: string): string {
        validateIdentifier('group', groupId); const deviceId = this.options.deviceId(); validateIdentifier('device', deviceId);
        return `Meshline/${this.options.context}/${this.options.accountId}/${deviceId}/group/${groupId}/${kind}/${key}`;
    }
    async #protect(secret: Uint8Array, purpose: string, signal?: AbortSignal): Promise<string> {
        try {
            const protectedData = await this.options.protector.protect(secret, purpose, signal); throwIfAborted(signal);
            if (!(protectedData instanceof Uint8Array) || !protectedData.length) throw new Error('Secret protector returned empty data.'); return encodeBase64Url(protectedData);
        } catch (cause) { throwIfAborted(signal); throw new GroupKeyAccessError('Local group secret protection failed.', { cause }); }
    }
    async #unprotect(value: JsonObject, field: string, purpose: string, signal?: AbortSignal): Promise<Uint8Array> {
        let result: Uint8Array | undefined;
        try {
            if (typeof value[field] !== 'string') throw new Error('Protected group secret is missing.');
            result = await this.options.protector.unprotect(decodeBase64Url(value[field]), purpose, signal); throwIfAborted(signal); requireLength(result, 32, 'Protected group secret'); return result;
        } catch (cause) { if (result instanceof Uint8Array) result.fill(0); throwIfAborted(signal); throw new GroupKeyAccessError('Local group secret could not be restored.', { cause }); }
    }
    async prepareMemberState(source: GroupMemberPrivateState, signal?: AbortSignal): Promise<PreparedGroupMemberKey> {
        const value = groupMemberPrivateStateCodec.decode(groupMemberPrivateStateCodec.encode(source));
        try {
            validateGroupMemberPrivateState(value); const deviceId = this.options.deviceId();
            const group = { groupId: value.groupId, relayId: value.relayId }; const publicKey = encryptionPublicKey(value.memberEncryptionPrivateKey);
            const purpose = this.#purpose(group.groupId, 'member', encodeBase64Url(publicKey));
            const protectedKey = await this.#protect(value.memberEncryptionPrivateKey, purpose, signal);
            if (purpose !== this.#purpose(group.groupId, 'member', encodeBase64Url(publicKey))) throw new GroupKeyAccessError('Local device changed during group key protection.');
            return { group, publicKey, protectedKey, deviceId };
        } finally { value.memberEncryptionPrivateKey.fill(0); }
    }
    /** Current and historical private keys coexist by derived public key; arrival order has no authority. */
    async saveMemberState(source: GroupMemberPrivateState, signal?: AbortSignal): Promise<void> {
        const effect = await this.prepareMemberStates([source], signal);
        await updateStore(this.options.store, effect.queries, snapshot => ({ mutations: effect.plan(snapshot), result: undefined }), signal);
    }
    /** Prepared mutations can join an account-message consumer cursor in one atomic commit. */
    async prepareMemberStates(sources: readonly GroupMemberPrivateState[], signal?: AbortSignal): Promise<GroupSecretEffect> {
        const states = sources.map(source => groupMemberPrivateStateCodec.decode(groupMemberPrivateStateCodec.encode(source)));
        try {
            validateAccountGroupPrivateStateSync({ states }); const prepared: PreparedGroupMemberKey[] = [];
            for (const state of states) prepared.push(await this.prepareMemberState(state, signal));
            const queries = prepared.flatMap(value => [groupKey(value.group), groupMemberSecretKey(value.group.groupId, value.publicKey)]);
            return { queries, plan: snapshot => {
                const mutations: StoreMutation[] = [];
                for (const [index, value] of prepared.entries()) {
                    if (this.options.deviceId() !== value.deviceId) throw new GroupKeyAccessError('Local device changed before member key persistence.');
                    const record = snapshot.sets[index * 2]![0]?.value; const existing = snapshot.sets[index * 2 + 1]![0]?.value; const publicKey = encodeBase64Url(value.publicKey);
                    if (record && record.relayId !== value.group.relayId) throw new ProtocolError('invalid_binding', 'Private group state names another hosting relay.');
                    if (existing && existing.publicKey !== publicKey) throw new ProtocolError('invalid_storage', 'Stored group member key does not match its identity.');
                    if (!record) mutations.push(put(groupKey(value.group), { relayId: value.group.relayId }));
                    if (!existing) mutations.push(put(groupMemberSecretKey(value.group.groupId, value.publicKey), { publicKey, protectedKey: value.protectedKey, shared: false }));
                }
                return mutations;
            } };
        } finally { for (const state of states) state.memberEncryptionPrivateKey.fill(0); }
    }
    async readMemberKey(groupId: string, publicKey: Uint8Array, signal?: AbortSignal): Promise<Uint8Array | undefined> {
        validateIdentifier('group', groupId); requireLength(publicKey, 32, 'Member public key'); const key = publicKey.slice();
        const row = (await this.options.store.read([groupMemberSecretKey(groupId, key)], signal)).sets[0]![0]?.value; if (!row) return undefined;
        const purpose = this.#purpose(groupId, 'member', encodeBase64Url(key)); const privateKey = await this.#unprotect(row, 'protectedKey', purpose, signal);
        try { if (row.publicKey !== encodeBase64Url(key) || !equalBytes(encryptionPublicKey(privateKey), key) || purpose !== this.#purpose(groupId, 'member', encodeBase64Url(key))) throw new GroupKeyAccessError('Restored group member key differs from its public identity.'); return privateKey; }
        catch (error) { privateKey.fill(0); throw error; }
    }
    async stageHistorySecret(source: GroupHistorySecret, signal?: AbortSignal): Promise<void> {
        const effect = await this.prepareHistorySecrets([source], signal);
        await updateStore(this.options.store, effect.queries, snapshot => ({ mutations: effect.plan(snapshot), result: undefined }), signal);
    }
    async prepareHistorySecrets(sources: readonly GroupHistorySecret[], signal?: AbortSignal): Promise<GroupSecretEffect> {
        const secrets = sources.map(source => groupHistorySecretCodec.decode(groupHistorySecretCodec.encode(source)));
        try {
            validateAccountGroupHistorySecretSync({ secrets }); const deviceId = this.options.deviceId();
            const queries = secrets.map(value => groupApplicationSecretKey(value.groupId, value.epoch)); const initial = await this.options.store.read(queries, signal); const mutations: StoreMutation[] = [];
            for (const [index, value] of secrets.entries()) {
                const row = initial.sets[index]![0]?.value; const purpose = this.#purpose(value.groupId, 'epoch', String(value.epoch));
                if (row) {
                    if (row.epoch !== value.epoch) throw new ProtocolError('invalid_storage', 'Stored group secret belongs to another epoch.');
                    const existing = await this.#unprotect(row, 'protectedSecret', purpose, signal);
                    try { if (!equalBytes(existing, value.applicationSecret)) throw new ProtocolError('conflicting_epoch_secret', 'Account synchronization conflicts with a previously stored epoch secret.'); }
                    finally { existing.fill(0); }
                } else {
                    const protectedSecret = await this.#protect(value.applicationSecret, purpose, signal);
                    mutations.push(put(queries[index]!, { epoch: value.epoch, protectedSecret, source: 'account', shared: false }));
                }
                if (deviceId !== this.options.deviceId()) throw new GroupKeyAccessError('Local device changed during epoch secret preparation.');
            }
            return { queries, plan: snapshot => {
                if (deviceId !== this.options.deviceId()) throw new GroupKeyAccessError('Local device changed before epoch secret persistence.');
                if (snapshot.sets.some((rows, index) => fingerprint(rows[0]?.value) !== fingerprint(initial.sets[index]![0]?.value))) throw new StateConflictError('Group epoch secret changed while synchronization was being prepared.');
                return mutations;
            } };
        } finally { for (const value of secrets) value.applicationSecret.fill(0); }
    }
    async readApplicationSecret(group: GroupRef, epoch: number, signal?: AbortSignal): Promise<Uint8Array | undefined> {
        validateGroupRef(group); requireSafeInteger(epoch, 0); const snapshot = await this.options.store.read([groupKey(group), groupEpochKey(group, epoch), groupApplicationSecretKey(group.groupId, epoch)], signal);
        const association = snapshot.sets[0]![0]?.value; if (association && association.relayId !== group.relayId) throw new ProtocolError('invalid_binding', 'Group secret belongs to another hosting relay.');
        if (!association?.projection || !snapshot.sets[1]![0]?.value.commitment) return undefined;
        const projection = decodeGroupProjection(requireObject(association.projection), group); if (epoch > projection.epoch) return undefined;
        const row = snapshot.sets[2]![0]?.value; if (!row) return undefined; if (row.epoch !== epoch) throw new ProtocolError('invalid_storage', 'Stored group secret belongs to another epoch.');
        const purpose = this.#purpose(group.groupId, 'epoch', String(epoch)); const secret = await this.#unprotect(row, 'protectedSecret', purpose, signal);
        if (purpose !== this.#purpose(group.groupId, 'epoch', String(epoch))) { secret.fill(0); throw new GroupKeyAccessError('Local device changed during group secret restoration.'); } return secret;
    }
    async readClientSecret(group: GroupRef, commitment: string, signal?: AbortSignal): Promise<Uint8Array | undefined> {
        validateGroupRef(group); contentHashBytes(commitment); const epochs = await new GroupRepository(this.options.store, this.options.context, this.options.accountId).epochs(group, signal);
        if (!epochs.some(epoch => epoch.commitment === commitment)) return undefined;
        const row = (await this.options.store.read([clientSecretKey(group.groupId, commitment)], signal)).sets[0]![0]?.value; if (!row) return undefined;
        const purpose = this.#purpose(group.groupId, 'client', commitment); const secret = await this.#unprotect(row, 'protectedSecret', purpose, signal);
        if (groupClientSecretCommitment(group.groupId, secret, this.options.context) !== commitment || purpose !== this.#purpose(group.groupId, 'client', commitment)) { secret.fill(0); throw new GroupKeyAccessError('Stored client secret does not match its verified commitment or local device.'); } return secret;
    }
    async rotation(group: GroupRef, signal?: AbortSignal): Promise<GroupRotation | undefined> {
        group = { ...group }; validateGroupRef(group); const snapshot = await this.options.store.read([groupKey(group), groupRotationKey(group)], signal);
        if (snapshot.sets[0]![0]?.value.relayId !== group.relayId) throw new ProtocolError('invalid_binding', 'Rotation group has no matching hosting association.');
        const row = snapshot.sets[1]![0]?.value; return row && rotationRecord(row, group);
    }
    async beginRotation(group: GroupRef, baseCommitment: string, source: Uint8Array, member: PreparedGroupMemberKey | undefined, signal?: AbortSignal): Promise<GroupRotation> {
        group = { ...group }; validateGroupRef(group); contentHashBytes(baseCommitment); const secret = source.slice();
        try {
            const commitment = groupClientSecretCommitment(group.groupId, secret, this.options.context); if (commitment === baseCommitment) throw new ProtocolError('unchanged_secret', 'Rotation requires a fresh client group secret.');
            const deviceId = this.options.deviceId(); const purpose = this.#purpose(group.groupId, 'rotation', commitment); const protectedSecret = await this.#protect(secret, purpose, signal);
            if (member && (member.group.groupId !== group.groupId || member.group.relayId !== group.relayId || member.deviceId !== deviceId)) throw new ProtocolError('invalid_binding', 'Rotation member key belongs to another group or local device.');
            const queries = [groupKey(group), groupRotationKey(group), ...(member ? [groupMemberSecretKey(group.groupId, member.publicKey)] : [])];
            return await updateStore(this.options.store, queries, snapshot => {
                if (purpose !== this.#purpose(group.groupId, 'rotation', commitment)) throw new GroupKeyAccessError('Local rotation protection identity changed.');
                const row = snapshot.sets[0]![0]?.value; if (row?.relayId !== group.relayId || !row.projection) throw new ProtocolError('unverified_group', 'Rotation requires verified group history.');
                const state = decodeGroupProjection(requireObject(row.projection), group);
                if (state.clientSecretCommitment !== baseCommitment || state.state.owner !== this.options.accountId || state.state.status !== 'active') throw new StateConflictError('Verified group secret or owner changed before rotation staging.');
                if (snapshot.sets[1]!.length) throw new StateConflictError('A group secret rotation is already staged.');
                const value: JsonObject = { relayId: group.relayId, baseCommitment, commitment, protectedSecret, deviceId, ownerSequence: row.ownerSequence ?? 0, ...(member ? { ownerPublicKey: encodeBase64Url(member.publicKey) } : {}) };
                const result = rotationRecord(value, group); const mutations: StoreMutation[] = [put(groupRotationKey(group), value)];
                if (member) { if (snapshot.sets[2]!.length) throw new StateConflictError('Candidate owner key already exists.'); mutations.push(put(queries[2]!, { publicKey: encodeBase64Url(member.publicKey), protectedKey: member.protectedKey, shared: false })); }
                return { mutations, result };
            }, signal);
        } finally { secret.fill(0); }
    }
    async readRotationSecret(rotation: GroupRotation, signal?: AbortSignal): Promise<Uint8Array> {
        const row = (await this.options.store.read([groupRotationKey(rotation.group)], signal)).sets[0]![0]?.value;
        if (!row || row.commitment !== rotation.commitment || row.baseCommitment !== rotation.baseCommitment) throw new StateConflictError('Candidate group secret changed before restoration.');
        const current = rotationRecord(row, rotation.group); if (current.deviceId !== this.options.deviceId()) throw new GroupKeyAccessError('Rotation belongs to another local device.');
        const purpose = this.#purpose(rotation.group.groupId, 'rotation', rotation.commitment); const secret = await this.#unprotect(row, 'protectedSecret', purpose, signal);
        if (purpose !== this.#purpose(rotation.group.groupId, 'rotation', rotation.commitment) || groupClientSecretCommitment(rotation.group.groupId, secret, this.options.context) !== rotation.commitment) { secret.fill(0); throw new GroupKeyAccessError('Restored candidate secret differs from its commitment or local identity.'); } return secret;
    }
    async acknowledgeRotation(rotation: GroupRotation, expiresAt: number): Promise<GroupRotation> {
        requireSafeInteger(expiresAt, 1);
        return updateStore(this.options.store, [groupRotationKey(rotation.group)], snapshot => {
            const value = snapshot.sets[0]![0]?.value; if (!value || value.commitment !== rotation.commitment) throw new StateConflictError('Candidate rotation changed before preparation was acknowledged.');
            const current = rotationRecord(value, rotation.group); if (current.expiresAt !== undefined && current.expiresAt !== expiresAt) throw new ProtocolError('conflicting_expiry', 'Relay changed the original rotation preparation expiry.');
            const next = { ...value, expiresAt }; return { mutations: [put(groupRotationKey(rotation.group), next)], result: rotationRecord(next, rotation.group) };
        });
    }
    async discardRotation(rotation: GroupRotation, signal?: AbortSignal): Promise<void> {
        await updateStore(this.options.store, [groupRotationKey(rotation.group)], snapshot => {
            const row = snapshot.sets[0]![0]?.value; if (row && row.commitment !== rotation.commitment) throw new StateConflictError('Another rotation replaced the obsolete candidate.');
            return { mutations: row ? [{ kind: 'delete', ...groupRotationKey(rotation.group) }] : [], result: undefined };
        }, signal);
    }
    async derivePending(group: GroupRef, device: MessageDecryptor, signal?: AbortSignal): Promise<GroupKeyRecoveryResult> {
        const epochs = await new GroupRepository(this.options.store, this.options.context, this.options.accountId).epochs(group, signal);
        const derived: number[] = []; const missing: number[] = []; const rejected: { epoch: number; error: ProtocolError }[] = [];
        for (const epoch of epochs) {
            if (!epoch.keyEntry || !epoch.memberPublicKey) continue;
            try { const result = await this.#deriveEpoch(group, epoch.epoch, device, signal); if (result === 'derived') derived.push(epoch.epoch); if (result === 'missing') missing.push(epoch.epoch); }
            catch (error) {
                throwIfAborted(signal);
                if (!(error instanceof ProtocolError) || !['invalid_ciphertext', 'invalid_key', 'invalid_commitment', 'conflicting_epoch_secret'].includes(error.code)) throw error;
                rejected.push({ epoch: epoch.epoch, error });
            }
        }
        return { derived, missing, rejected };
    }
    async #deriveEpoch(group: GroupRef, epoch: number, device: MessageDecryptor, signal?: AbortSignal): Promise<'derived' | 'missing' | 'ready'> {
        const { store, context, accountId } = this.options; const first = (await store.read([groupEpochKey(group, epoch)], signal)).sets[0]![0]?.value;
        if (!first || typeof first.commitment !== 'string' || typeof first.memberPublicKey !== 'string') throw new ProtocolError('invalid_storage', 'Group epoch has no verified secret authority.');
        const commitment = first.commitment; contentHashBytes(commitment); const publicKey = decodeBase64Url(first.memberPublicKey); requireLength(publicKey, 32, 'Verified member public key');
        const queries = [groupKey(group), groupEpochKey(group, epoch), groupMemberSecretKey(group.groupId, publicKey), groupApplicationSecretKey(group.groupId, epoch), clientSecretKey(group.groupId, commitment)];
        const snapshot = await store.read(queries, signal); const [association, epochRow, member, application, client] = snapshot.sets.map(rows => rows[0]?.value);
        if (association?.relayId !== group.relayId || !association.projection || !epochRow || fingerprint(epochRow) !== fingerprint(first)) throw new StateConflictError('Group key authority changed during recovery.');
        if (decodeGroupProjection(requireObject(association.projection), group).epoch < epoch) throw new ProtocolError('invalid_storage', 'Group epoch has not been reached by verified history.');
        const certificate = deviceCertificateCodec.decode(deviceCertificateCodec.encode(device.certificate)); validateCertificate(certificate, context); const deviceId = certificateId(certificate, context);
        if (certificate.account !== accountId || deviceId !== this.options.deviceId()) throw new GroupKeyAccessError('Group key receiver belongs to another local account or device.');
        if (epochRow.materialVerified === true && application && client) return 'ready';
        if (!member) return 'missing';
        const entry = groupKeyEntryCodec.decode(epochRow.keyEntry!); validateGroupKeyEntry(entry);
        if (entry.epoch !== epoch || !entry.clientSecretBox || member.publicKey !== encodeBase64Url(publicKey)) throw new ProtocolError('invalid_storage', 'Stored group key entry or member key has inconsistent metadata.');
        const memberPurpose = this.#purpose(group.groupId, 'member', encodeBase64Url(publicKey)); const clientPurpose = this.#purpose(group.groupId, 'client', commitment); const epochPurpose = this.#purpose(group.groupId, 'epoch', String(epoch));
        const privateKey = await this.#unprotect(member, 'protectedKey', memberPurpose, signal);
        let shared: Uint8Array | undefined; let clientSecret: Uint8Array | undefined; let relaySecret: Uint8Array | undefined; let secret: Uint8Array | undefined; let oldApplication: Uint8Array | undefined; let oldClient: Uint8Array | undefined;
        try {
            if (!equalBytes(encryptionPublicKey(privateKey), publicKey)) throw new GroupKeyAccessError('Restored member key differs from its verified public key.');
            shared = agreeKey(privateKey, entry.clientSecretBox.enc);
            clientSecret = openGroupSecret(entry.clientSecretBox, shared, groupClientSecretBoxAad({ groupId: group.groupId, account: accountId, memberEncryptionPublicKey: publicKey, clientSecretCommitment: commitment }, context));
            if (groupClientSecretCommitment(group.groupId, clientSecret, context) !== commitment) throw new ProtocolError('invalid_commitment', 'Unwrapped client secret differs from the verified commitment.');
            shared.fill(0); shared = undefined;
            const probe = agreeKey(new Uint8Array(32).fill(1), entry.relaySecretBox.enc); probe.fill(0);
            try {
                shared = await device.deriveSharedSecret(entry.relaySecretBox.enc.slice(), signal); throwIfAborted(signal); requireLength(shared, 32, 'Device shared secret');
                if (shared.every(byte => byte === 0) || certificateId(device.certificate, context) !== deviceId) throw new Error('Device key agreement returned invalid identity or key material.');
            } catch (cause) { throwIfAborted(signal); throw new GroupKeyAccessError('Local device could not open the group relay-secret box.', { cause }); }
            relaySecret = openGroupSecret(entry.relaySecretBox, shared, groupRelaySecretBoxAad({ groupId: group.groupId, account: accountId, deviceId, epoch }, context));
            secret = deriveGroupApplicationSecret(group.groupId, epoch, commitment, clientSecret, relaySecret, context);
            if (application) { oldApplication = await this.#unprotect(application, 'protectedSecret', epochPurpose, signal); if (!equalBytes(oldApplication, secret)) throw new ProtocolError('conflicting_epoch_secret', 'Derived group secret conflicts with a previously synchronized epoch secret.'); }
            if (client) { oldClient = await this.#unprotect(client, 'protectedSecret', clientPurpose, signal); if (!equalBytes(oldClient, clientSecret)) throw new GroupKeyAccessError('Stored client secret conflicts with its verified commitment.'); }
            const protectedSecret = application?.protectedSecret ?? await this.#protect(secret, epochPurpose, signal);
            const protectedClient = client?.protectedSecret ?? await this.#protect(clientSecret, clientPurpose, signal);
            await updateStore(store, queries, current => {
                if (this.options.deviceId() !== deviceId) throw new GroupKeyAccessError('Local device changed before epoch secret persistence.');
                if (current.sets.some((rows, i) => fingerprint(rows[0]?.value) !== fingerprint(snapshot.sets[i]![0]?.value))) throw new StateConflictError('Group key material changed while recovery was being prepared.');
                return { mutations: [put(queries[1]!, { ...epochRow, materialVerified: true }), put(queries[3]!, { ...application, epoch, protectedSecret, source: 'derived' }),
                    put(queries[4]!, { commitment, protectedSecret: protectedClient })], result: undefined };
            }, signal);
            return 'derived';
        } finally {
            privateKey.fill(0); if (shared instanceof Uint8Array) shared.fill(0); clientSecret?.fill(0); relaySecret?.fill(0); secret?.fill(0); oldApplication?.fill(0); oldClient?.fill(0);
        }
    }
}
