import { encryptGroupMessage, groupClientSecretCommitment, GroupKeyAccessError, sealGroupClientSecret } from '../crypto/groups.js';
import { requireLength, systemRandom, verifyDevice, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import { GroupMessageProcessor, type GroupMessageInfo } from '../groups/messages.js';
import { GroupAccountSync } from '../groups/account-sync.js';
import { GroupOperations, groupOperationMethods, groupOperationPayload, type GroupOperation, type GroupOperationMethod } from '../groups/operations.js';
import { decodeGroupProjection, GroupRepository, groupKey } from '../groups/repository.js';
import { GroupSecrets, type PreparedGroupMemberKey } from '../groups/secrets.js';
import { applyGroupEvent, type GroupChangeKind, type GroupProjection } from '../groups/state.js';
import { createIdentifier, deriveResourceId, validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId, validateRelayId } from '../identity/neo.js';
import type { SecretProtector } from '../interactions.js';
import { snapshotReader } from '../messages/repository.js';
import { groupKeyPageCodec, groupRotationPrepareRequestCodec, groupRotationPrepareResultCodec, validateGroupRotationPrepareRequest, validateGroupRotationPrepareResult } from '../models/group-keys.js';
import { groupChangedNotificationCodec, groupInvitePageCodec, groupInviteQueryCodec, groupInviteResolveResultCodec, groupListQueryCodec, groupTimelineChangedNotificationCodec, validateGroupChangedNotification,
    validateGroupInvitePage, validateGroupInviteQuery, validateGroupInviteResolveResult, validateGroupListQuery, validateGroupTimelineChangedNotification } from '../models/group-admission.js';
import * as admission from '../models/group-admission.js';
import { groupApplicationCodec, groupCreateRequestCodec, groupEventCodec, groupInviteCodec, groupInviteInput, groupManagementInput, groupManagementPayloadCodec, groupMemberRecoveryRequestCodec, groupSequenceQueryCodec, groupStateCodec, groupSyncPageCodec, validateGroupApplication, validateGroupInvite, validateGroupManagementPayload, validateGroupMemberRecoveryRequest, validateGroupState, verifyGroupApplication, verifyGroupInvite, verifyGroupManagementPayload, verifyGroupMemberRecoveryRequest,
    type GroupInvite, type GroupInvitePolicy, type GroupManagementPayload, type GroupRole, type GroupState, type GroupUpdate } from '../models/group-management.js';
import { groupMessageCodec, groupMessageEnvelopeCodec, groupMemberNicknameUpdateCodec, validateGroupPayload, validateGroupRef, type GroupMemberKey, type GroupMessage, type GroupRef, type GroupSecretBox } from '../models/groups.js';
import { certificateId } from '../models/identity.js';
import { signingInput } from '../protocol/context.js';
import type { ProtocolCodec } from '../protocol/codec.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { AsyncPulse } from '../runtime/async-pulse.js';
import { throwIfAborted } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import type { MeshlineStore, QueryReader } from '../storage/store.js';
import type { RelayClient } from '../transport/client.js';
import type { RelayClientPool } from '../transport/pool.js';
import { RelayError } from '../transport/relay-error.js';
import type { RelaySubscription } from '../transport/subscription.js';
import { ClientComponent, type ClientOptions } from './component.js';
import type { DeviceManager } from './device.js';
import type { MessageManager } from './message.js';

function ordinal(left: string, right: string): number { return left < right ? -1 : left > right ? 1 : 0; }

export interface GroupManagerOptions extends ClientOptions { readonly store: MeshlineStore; readonly relayClients: RelayClientPool; readonly deviceManager: DeviceManager; readonly messageManager: MessageManager; readonly secretProtector: SecretProtector; readonly random?: RandomSource }
export interface GroupCreateOptions { readonly name: string; readonly description?: string; readonly memberCapacity: number; readonly invitePolicy?: GroupInvitePolicy }
export type GroupUpdateOptions = Pick<GroupUpdate, 'name' | 'description' | 'memberCapacity' | 'invitePolicy'>;
export type GroupMessageDraft = GroupMessage;
export type GroupMembershipState = 'unknown' | 'notMember' | 'pending' | 'member' | 'left' | 'removed' | 'banned';
export interface GroupInfo { readonly ref: GroupRef; readonly group: GroupState; readonly membership: GroupMembershipState; readonly role?: GroupRole }
export type { GroupChangeKind } from '../groups/state.js';
export interface GroupChange extends GroupInfo { readonly kinds: readonly GroupChangeKind[] }
export interface GroupMemberInfo { readonly accountId: string; readonly role: GroupRole; readonly nickname?: string; readonly memberEncryptionPublicKey: Uint8Array }
export interface GroupInvitation { readonly group: GroupRef; readonly document: GroupInvite }
export interface GroupInviteRef { readonly group: GroupRef; readonly inviteId: string }
export interface GroupInviteInfo { readonly invite: GroupInvitation; readonly uses: number }
export interface GroupInviteOptions { readonly expiresAt: number; readonly invitee?: string; readonly maxUses?: number }
export interface GroupPageRequest { readonly cursor?: string; readonly limit?: number }
export interface GroupPage<T> { readonly items: readonly T[]; readonly nextCursor?: string }
export interface GroupApplicationInfo extends admission.GroupApplicationEntry { readonly group: GroupRef }
export interface GroupKeyRecoveryInfo extends admission.GroupRecoveryEntry { readonly group: GroupRef }
export interface GroupEvents {
    readonly groupChanged: GroupChange;
    readonly timelineChanged: { readonly group: GroupRef; readonly sequence: number; readonly message?: GroupMessageInfo };
    readonly applicationsChanged: GroupRef;
    readonly keyRecoveryChanged: GroupRef;
}
interface Subscription { client: RelayClient; subscription: RelaySubscription; selected: Set<string>; detach: (() => void)[] }

/** Verified history determines authority; local secrets and exact signed submissions survive restart. */
export class GroupManager extends ClientComponent {
    readonly #store: MeshlineStore; readonly #pool: RelayClientPool; readonly #device: DeviceManager; readonly #random: RandomSource;
    readonly #repository: GroupRepository; readonly #secrets: GroupSecrets; readonly #messages: GroupMessageProcessor; readonly #operations: GroupOperations;
    readonly #accountMessages: MessageManager; readonly #accountSync: GroupAccountSync; #detachMessages: (() => void) | undefined;
    readonly #writes = new AsyncGate(); readonly #events = new EventHub<GroupEvents>(); readonly #subscriptions = new Map<string, Subscription>();
    #pulse: AsyncPulse | undefined; #jobs: Promise<void>[] = [];
    constructor(options: GroupManagerOptions) {
        super(options);
        for (const dependency of [options.relayClients, options.deviceManager, options.messageManager]) if (dependency.accountId !== this.accountId || dependency.context.toString() !== this.context.toString()) throw new ProtocolError('invalid_context', 'Group dependencies belong to another network or account.');
        this.#store = options.store; this.#pool = options.relayClients; this.#device = options.deviceManager; this.#random = options.random ?? systemRandom;
        this.#repository = new GroupRepository(this.#store, this.context, this.accountId);
        this.#secrets = new GroupSecrets({ store: this.#store, context: this.context, accountId: this.accountId, deviceId: () => certificateId(this.#device.certificate, this.context), protector: options.secretProtector });
        this.#messages = new GroupMessageProcessor(this.#store, this.context, this.accountId, this.#secrets); this.#operations = new GroupOperations(this.#store, this.context, this.accountId);
        this.#accountMessages = options.messageManager;
        this.#accountSync = new GroupAccountSync({ store: this.#store, context: this.context, accountId: this.accountId, clock: this.clock, device: this.#device, messages: this.#accountMessages, secrets: this.#secrets,
            rejected: (messageId, error) => this.notifyBackgroundError({ operation: 'synchronize_group_account', resource: messageId, error }) });
    }
    on<K extends keyof GroupEvents>(event: K, listener: EventListener<GroupEvents[K]>): () => void { return this.#events.on(event, listener); }
    #notify<K extends keyof GroupEvents>(event: K, value: GroupEvents[K]): void { this.#events.notify(event, value, error => this.notifyBackgroundError({ operation: 'observer', resource: event, error })); }
    protected override async onInitialize(signal: AbortSignal): Promise<void> { this.#device.ensureInitialized(); this.#accountMessages.ensureInitialized(); await this.#store.initialize({ context: this.context.toString(), accountId: this.accountId }, signal); }
    async #hosting(relayId: string, signal: AbortSignal): Promise<RelayClient> {
        validateRelayId(relayId); const relay = await this.#pool.get(relayId, { mode: 'device', signer: this.#device }, signal);
        if (!(await relay.getDescriptor(signal)).capabilities?.includes('group.host.v1')) throw new ProtocolError('unsupported_capability', 'Relay does not host groups.'); return relay;
    }
    #snapshot(group: GroupRef, row: JsonObject): GroupInfo | undefined {
        if (row.relayId !== group.relayId) throw new ProtocolError('invalid_binding', 'Group belongs to another hosting relay.');
        if (!row.projection) {
            if (!row.preview) return undefined; const preview = groupStateCodec.decode(row.preview); validateGroupState(preview);
            if (preview.groupId !== group.groupId) throw new ProtocolError('invalid_storage', 'Stored preview belongs to another group.');
            return { ref: group, group: preview, membership: row.pendingApplication === true ? 'pending' : 'unknown' };
        }
        const projection = decodeGroupProjection(requireObject(row.projection), group); const member = projection.members.find(value => value.account === this.accountId);
        const departure = projection.departures[this.accountId]; let membership: GroupMembershipState = member ? 'member' : departure === 'not_member' ? 'notMember' : departure ?? 'notMember';
        if (!member && row.pendingApplication === true && membership !== 'banned') membership = 'pending';
        if (row.localDepartureAfter !== undefined) { requireSafeInteger(row.localDepartureAfter, 0); if (projection.sequence <= row.localDepartureAfter) membership = 'left'; }
        return { ref: { ...group }, group: row.locallyClosed === true ? { ...projection.state, status: 'closed' } : projection.state, membership, ...(membership === 'member' ? { role: member!.role } : {}) };
    }
    async #info(group: GroupRef, signal?: AbortSignal): Promise<GroupInfo> {
        const row = (await this.#store.read([groupKey(group)], signal)).sets[0]![0]?.value; const result = row && this.#snapshot(group, row);
        if (!result) throw new ProtocolError('unverified_group', 'Group history has not established a verified state.'); return result;
    }
    async #writable(group: GroupRef, signal: AbortSignal): Promise<GroupProjection> {
        const info = await this.#info(group, signal);
        if (info.group.status !== 'active' || info.membership !== 'member') throw new ProtocolError('forbidden', 'Group is not writable by this account.'); return (await this.#repository.get(group, signal))!;
    }
    async #sign<T extends GroupManagementPayload>(source: T, signal: AbortSignal): Promise<T> {
        const payload = groupManagementPayloadCodec.decode(groupManagementPayloadCodec.encode(source)); validateGroupManagementPayload(payload);
        const certificate = this.#device.certificate; const id = certificateId(certificate, this.context);
        const signature = await this.#device.sign(groupManagementInput(payload, this.context), signal); throwIfAborted(signal);
        if (id !== certificateId(this.#device.certificate, this.context)) throw new StateConflictError('Group signing device changed during signing.');
        const result = { ...payload, value: { ...payload.value, deviceSignature: signature } } as T; verifyGroupManagementPayload(result, certificate, this.context); return result;
    }
    async #signRequest<T extends { readonly deviceSignature: Uint8Array }>(source: T, codec: ProtocolCodec<T>, signal: AbortSignal): Promise<T> {
        const copy = codec.decode(codec.encode(source)); const certificate = this.#device.certificate; const id = certificateId(certificate, this.context); const input = signingInput(codec.encode(copy), this.context, ['device_signature']);
        const signature = await this.#device.sign(input, signal); throwIfAborted(signal);
        if (id !== certificateId(this.#device.certificate, this.context) || !verifyDevice(input, signature, certificate.signingPublicKey)) throw new StateConflictError('Local member-request signer changed or returned an invalid signature.');
        return { ...copy, deviceSignature: signature };
    }
    #checkManagement(state: GroupProjection, payload: GroupManagementPayload): void {
        const advances = ['applicationApproval', 'memberRecoveryApproval', 'memberLeave', 'memberRemoval', 'secretRotation'].includes(payload.kind)
            || payload.kind === 'memberBan' && payload.value.accounts.some(account => state.members.some(member => member.account === account));
        applyGroupEvent(state, { sequence: state.sequence + 1, epoch: state.epoch + (advances ? 1 : 0), acceptedAt: this.clock.nowSeconds(), signerDeviceId: certificateId(this.#device.certificate, this.context), payload: requireObject(groupManagementPayloadCodec.encode(payload)) }, this.#device.certificate, state, this.context);
    }
    #operationChanged(operation: GroupOperation): void {
        if (operation.method.startsWith('group.application.')) this.#notify('applicationsChanged', operation.group);
        if (operation.method.startsWith('group.member.recovery.')) this.#notify('keyRecoveryChanged', operation.group);
    }
    async #localRecovery(group: GroupRef, signal: AbortSignal): Promise<void> {
        const recovery = await this.#secrets.derivePending(group, this.#device, signal);
        for (const failure of recovery.rejected) this.notifyBackgroundError({ operation: 'recover_group_key', resource: `${group.groupId}/${failure.epoch}`, error: failure.error });
        await this.#messages.processPending(group, (sequence, result) => {
            if (result.error) this.notifyBackgroundError({ operation: 'decrypt_group', resource: `${group.groupId}/${sequence}`, error: result.error });
            if (result.groupRecord) this.#notify('groupChanged', { ...this.#snapshot(group, result.groupRecord)!, kinds: ['nickname'] });
            this.#notify('timelineChanged', { group, sequence, ...(result.message ? { message: result.message } : {}) });
        }, signal);
    }
    async #synchronize(relay: RelayClient, group: GroupRef, signal: AbortSignal, includeKeys = true): Promise<void> {
        let projection: GroupProjection | undefined;
        for (;;) {
            const page = await this.#repository.synchronizePage(group, { read: async query => groupSyncPageCodec.decode((await relay.requestHttp('GET', 'group.sync', groupSequenceQueryCodec.encode(query), { signal }))!) }, signal);
            projection = page.projection; for (const failure of page.rejected) this.notifyBackgroundError({ operation: 'synchronize_group', resource: `${group.groupId}/${failure.sequence}`, error: failure.error });
            if (page.change) this.#notify('groupChanged', { ...this.#snapshot(group, page.change.record)!, kinds: page.change.kinds });
            if (!page.hasMore) break;
        }
        if (!includeKeys || !projection) return;
        if (projection.members.some(member => member.account === this.accountId)) {
            let after = await this.#repository.keyProgress(group, signal);
            while (after < projection.epoch) {
                const page = groupKeyPageCodec.decode((await relay.requestHttp('GET', 'group.key.sync', groupSequenceQueryCodec.encode({ groupId: group.groupId, after }), { signal }))!);
                await this.#repository.saveKeyPage(group, page, after, signal); if (!page.hasMore) break; after = page.keys.at(-1)!.epoch;
            }
        }
        await this.#localRecovery(group, signal);
        await this.#accountSync.shareCurrent(group, signal);
    }
    async #execute(relay: RelayClient, initial: GroupOperation, recovering: boolean, signal: AbortSignal): Promise<JsonObject | undefined> {
        let operation = initial;
        const timelinePayload = groupOperationPayload(operation);
        const unconfirmedMemberRequest = operation.method === 'group.application.submit' && !operation.accepted
            || operation.method === 'group.member.recovery.submit' && operation.acceptedResult === undefined;
        if (recovering && unconfirmedMemberRequest) {
            let approved = await this.#operations.acceptedMemberRequest(operation, signal);
            if (!approved) {
                try { await this.#synchronize(relay, operation.group, signal, false); }
                catch (error) { if (!(error instanceof RelayError && ['forbidden', 'not_found'].includes(error.code))) throw error; }
                approved = await this.#operations.acceptedMemberRequest(operation, signal);
            }
            if (approved) {
                // The signed history already owns membership, including any later departure
                // or key replacement. Completing the request must not mark it pending again.
                await this.#operations.complete(operation); this.#operationChanged(operation); return undefined;
            }
        }
        if (recovering && operation.method === 'group.invite.create' && !operation.accepted) {
            const expected = groupInviteCodec.decode(operation.request);
            try {
                const existing = await this.#readInvite({ group: operation.group, inviteId: expected.inviteId }, signal);
                if (groupInviteCodec.stringify(existing.invite.document) !== groupInviteCodec.stringify(expected)) throw new StateConflictError('Invitation identifier is occupied by different signed content.');
                await this.#operations.complete(operation); return undefined;
            } catch (error) { if (!(error instanceof RelayError && error.code === 'not_found')) throw error; }
        }
        if (recovering && timelinePayload) {
            try { await this.#synchronize(relay, operation.group, signal, false); }
            catch (error) {
                const noHistory = error instanceof RelayError && (error.code === 'not_found' && operation.method === 'group.create' || error.code === 'forbidden' && ['group.close', 'group.member.leave'].includes(operation.method));
                if (!noHistory) throw error;
            }
        }
        let evidence = await this.#operations.acceptedEvent(operation, signal);
        if (!evidence && !operation.accepted) {
            let result;
            try { result = await relay.requestHttp(groupOperationMethods[operation.method], operation.method, operation.request, { signal }); }
            catch (error) { if (!recovering && error instanceof RelayError && error.isDefinitiveRejection) await this.#operations.complete(operation); throw error; }
            let sequence: unknown;
            if (operation.method === 'group.message.send') {
                try { sequence = requireObject(result!).sequence; requireSafeInteger(sequence, 1); }
                catch (error) { await this.#operations.acknowledge(operation); throw error; }
            }
            let acceptedResult: JsonObject | undefined;
            if (operation.method === 'group.member.recovery.submit') {
                try { const value = admission.groupRecoverySubmitResultCodec.decode(result!); admission.validateGroupRecoverySubmitResult(value); acceptedResult = admission.groupRecoverySubmitResultCodec.encode(value); }
                catch (error) { await this.#operations.acknowledge(operation); throw error; }
            }
            operation = await this.#operations.acknowledge(operation, sequence as number | undefined, acceptedResult);
        }
        if (['group.close', 'group.member.leave'].includes(operation.method)) {
            const record = await this.#operations.complete(operation, true);
            if (record) this.#notify('groupChanged', { ...this.#snapshot(operation.group, record)!, kinds: [operation.method === 'group.close' ? 'status' : 'members'] }); return undefined;
        }
        if (!timelinePayload) {
            if (operation.method === 'group.member.recovery.submit' && operation.acceptedResult === undefined) throw new ProtocolError('unconfirmed_recovery', 'Accepted recovery request has no valid persisted acceptance interval.');
            const record = await this.#operations.complete(operation, operation.method === 'group.application.submit'); this.#operationChanged(operation);
            if (record && operation.method === 'group.application.submit') this.#notify('groupChanged', { ...this.#snapshot(operation.group, record)!, kinds: ['members'] }); return operation.acceptedResult;
        }
        await this.#synchronize(relay, operation.group, signal);
        evidence = await this.#operations.acceptedEvent(operation, signal);
        if (!evidence) throw new ProtocolError('unconfirmed_operation', 'Accepted group operation is absent from verified history.');
        if (operation.method === 'group.message.send') {
            if (evidence.record.rejection !== undefined) throw new ProtocolError('rejected_message', 'Accepted group message failed local content validation.');
            if (evidence.record.decryptedPayload === undefined) throw new GroupKeyAccessError('Accepted group message awaits an available epoch key.');
        }
        await this.#operations.complete(operation, true); this.#operationChanged(operation); return evidence.record;
    }
    createGroup(relayId: string, options: GroupCreateOptions, signal?: AbortSignal): Promise<GroupInfo> {
        validateRelayId(relayId); const copy = { ...options };
        return this.runOperation(scope => this.#writes.run(async () => {
            const relay = await this.#hosting(relayId, scope); const limit = (await relay.getInfo(scope)).limits.maxGroupMembers!;
            if (copy.memberCapacity > limit) throw new ProtocolError('invalid_capacity', 'Group capacity exceeds the hosting relay limit.');
            const nonce = this.#random.bytes(16); requireLength(nonce, 16, 'Group nonce'); const group = { relayId, groupId: deriveResourceId('group', this.accountId, relayId, nonce, this.context) };
            const privateKey = this.#random.bytes(32); let secret: Uint8Array | undefined;
            try {
                secret = this.#random.bytes(32);
                const member = await this.#secrets.prepareMemberState({ ...group, memberEncryptionPrivateKey: privateKey }, scope);
                const commitment = groupClientSecretCommitment(group.groupId, secret, this.context);
                const signed = await this.#sign({ kind: 'create', value: { ...copy, invitePolicy: copy.invitePolicy ?? 'administrators', groupId: group.groupId, nonce,
                    owner: { account: this.accountId, memberEncryptionPublicKey: member.publicKey }, clientSecretCommitment: commitment, deviceSignature: new Uint8Array(64) } }, scope);
                const request = groupCreateRequestCodec.encode({ create: signed.value, clientSecretBox: sealGroupClientSecret({ groupId: group.groupId, account: this.accountId, memberEncryptionPublicKey: member.publicKey, clientSecretCommitment: commitment }, secret, this.context, this.#random) });
                privateKey.fill(0); secret.fill(0);
                if (member.deviceId !== certificateId(this.#device.certificate, this.context)) throw new GroupKeyAccessError('Local group key protection device changed.');
                const operation: GroupOperation = { group, method: 'group.create', request }; await this.#operations.save(operation, member, scope); this.#pulse?.pulse();
                await this.#execute(relay, operation, false, scope); return this.#info(group, scope);
            } finally { privateKey.fill(0); secret?.fill(0); }
        }, scope), signal);
    }
    /** Invitation previews validate local fields and recipient; the host checks its accepted invitation by ID. Preview data grants no membership or key authority. */
    getGroup(source: GroupRef | GroupInvitation, signal?: AbortSignal): Promise<GroupInfo> {
        if ('document' in source) {
            const invitation = this.#copyInvitation(source);
            return this.runOperation(scope => this.#writes.run(async () => { const preview = await this.#previewInvitation(invitation, scope); await this.#repository.savePreview(invitation.group, preview.state, scope); return this.#info(invitation.group, scope); }, scope), signal);
        }
        const group = { ...source }; validateGroupRef(group);
        return this.runOperation(scope => this.#writes.run(async () => { await this.#synchronize(await this.#hosting(group.relayId, scope), group, scope); return this.#info(group, scope); }, scope), signal);
    }
    #manage(group: GroupRef, method: GroupOperationMethod, create: (state: GroupProjection) => GroupManagementPayload, signal?: AbortSignal): Promise<GroupInfo> {
        group = { ...group }; validateGroupRef(group);
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(group, method, scope); const relay = await this.#hosting(group.relayId, scope); await this.#synchronize(relay, group, scope);
            const state = await this.#writable(group, scope); const payload = await this.#sign(create(state), scope);
            this.#checkManagement(state, payload);
            const operation: GroupOperation = { group, method, request: requireObject(groupManagementPayloadCodec.encode(payload)) }; await this.#operations.save(operation, undefined, scope); this.#pulse?.pulse();
            await this.#execute(relay, operation, false, scope); return this.#info(group, scope);
        }, scope), signal);
    }
    updateGroup(group: GroupRef, update: GroupUpdateOptions, signal?: AbortSignal): Promise<GroupInfo> { const copy = { ...update }; return this.#manage(group, 'group.update', state => ({ kind: 'update', value: { ...copy, groupId: state.groupId, prevHash: state.managementHash, deviceSignature: new Uint8Array(64) } }), signal); }
    async closeGroup(group: GroupRef, signal?: AbortSignal): Promise<void> { await this.#manage(group, 'group.close', state => ({ kind: 'close', value: { groupId: state.groupId, prevHash: state.managementHash, deviceSignature: new Uint8Array(64) } }), signal); }
    async leaveGroup(group: GroupRef, signal?: AbortSignal): Promise<void> { await this.#manage(group, 'group.member.leave', state => ({ kind: 'memberLeave', value: { groupId: state.groupId, prevHash: state.managementHash, account: this.accountId, deviceSignature: new Uint8Array(64) } }), signal); }
    async removeMembers(group: GroupRef, accounts: readonly string[], signal?: AbortSignal): Promise<void> { const copy = [...accounts]; await this.#manage(group, 'group.member.remove', state => ({ kind: 'memberRemoval', value: { groupId: state.groupId, prevHash: state.managementHash, accounts: copy, deviceSignature: new Uint8Array(64) } }), signal); }
    async ban(group: GroupRef, accounts: readonly string[], signal?: AbortSignal): Promise<void> { const copy = [...accounts]; await this.#manage(group, 'group.member.ban', state => ({ kind: 'memberBan', value: { groupId: state.groupId, prevHash: state.managementHash, accounts: copy, deviceSignature: new Uint8Array(64) } }), signal); }
    async unban(group: GroupRef, accounts: readonly string[], signal?: AbortSignal): Promise<void> { const copy = [...accounts]; await this.#manage(group, 'group.member.unban', state => ({ kind: 'memberUnban', value: { groupId: state.groupId, prevHash: state.managementHash, accounts: copy, deviceSignature: new Uint8Array(64) } }), signal); }
    async setRole(group: GroupRef, account: string, role: Exclude<GroupRole, 'owner'>, signal?: AbortSignal): Promise<void> { await this.#manage(group, 'group.role.update', state => ({ kind: 'roleUpdate', value: { groupId: state.groupId, prevHash: state.managementHash, account, role, deviceSignature: new Uint8Array(64) } }), signal); }
    async transferOwnership(group: GroupRef, account: string, signal?: AbortSignal): Promise<void> { await this.#manage(group, 'group.owner.transfer', state => ({ kind: 'ownerTransfer', value: { groupId: state.groupId, prevHash: state.managementHash, newOwnerAccount: account, deviceSignature: new Uint8Array(64) } }), signal); }
    createInvite(group: GroupRef, options: GroupInviteOptions, signal?: AbortSignal): Promise<GroupInvitation> {
        group = { ...group }; validateGroupRef(group); const copy = { ...options };
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(group, 'group.invite.create', scope); const relay = await this.#hosting(group.relayId, scope); await this.#synchronize(relay, group, scope, false);
            const state = await this.#writable(group, scope); const role = state.members.find(member => member.account === this.accountId)!.role;
            if (role === 'member' && (state.state.invitePolicy === 'administrators' || copy.invitee === undefined && state.state.invitePolicy !== 'members_shareable')) throw new ProtocolError('forbidden', 'Current membership cannot issue this group invitation.');
            const now = this.clock.nowSeconds(); const limits = (await relay.getInfo(scope)).limits;
            if (copy.expiresAt - now > limits.maxGroupInviteTtl!) throw new ProtocolError('invalid_expiry', 'Invitation lifetime exceeds the hosting relay limit.');
            let invite: GroupInvite = { ...copy, groupId: group.groupId, inviteId: createIdentifier('invite', this.#random), inviter: this.accountId, createdAt: now, deviceSignature: new Uint8Array(64) }; validateGroupInvite(invite, now);
            const certificate = this.#device.certificate; const id = certificateId(certificate, this.context); invite = { ...invite, deviceSignature: await this.#device.sign(groupInviteInput(invite, this.context), scope) };
            if (id !== certificateId(this.#device.certificate, this.context)) throw new StateConflictError('Local invitation signer changed during signing.'); verifyGroupInvite(invite, certificate, this.context, now);
            const operation: GroupOperation = { group, method: 'group.invite.create', request: groupInviteCodec.encode(invite) }; await this.#operations.save(operation, undefined, scope); this.#pulse?.pulse(); await this.#execute(relay, operation, false, scope);
            return { group, document: invite };
        }, scope), signal);
    }
    async #readInvite(reference: GroupInviteRef, signal: AbortSignal): Promise<GroupInviteInfo> {
        const query = { groupId: reference.group.groupId, inviteId: reference.inviteId }; validateGroupInviteQuery(query);
        const relay = await this.#hosting(reference.group.relayId, signal); const result = groupInviteResolveResultCodec.decode((await relay.requestHttp('GET', 'group.invite.resolve', groupInviteQueryCodec.encode(query), { signal }))!);
        validateGroupInviteResolveResult(result, query, this.context, this.clock.nowSeconds()); return { invite: { group: reference.group, document: result.invite }, uses: result.uses };
    }
    getInvite(reference: GroupInviteRef, signal?: AbortSignal): Promise<GroupInviteInfo> {
        reference = { group: { ...reference.group }, inviteId: reference.inviteId }; validateGroupRef(reference.group); validateIdentifier('invite', reference.inviteId); return this.runOperation(scope => this.#readInvite(reference, scope), signal);
    }
    getInvites(group: GroupRef, page: GroupPageRequest = {}, signal?: AbortSignal): Promise<GroupPage<GroupInviteInfo>> {
        group = { ...group }; validateGroupRef(group); const query = { ...page, groupId: group.groupId }; validateGroupListQuery(query);
        return this.runOperation(async scope => {
            const relay = await this.#hosting(group.relayId, scope); const result = groupInvitePageCodec.decode((await relay.requestHttp('GET', 'group.invite.list', groupListQueryCodec.encode(query), { signal: scope }))!);
            validateGroupInvitePage(result, query, this.context, this.clock.nowSeconds()); return { items: result.invites.map(entry => ({ invite: { group, document: entry.invite }, uses: entry.uses })), ...(result.next === undefined ? {} : { nextCursor: result.next }) };
        }, signal);
    }
    revokeInvite(reference: GroupInviteRef, signal?: AbortSignal): Promise<void> {
        reference = { group: { ...reference.group }, inviteId: reference.inviteId }; validateGroupRef(reference.group); const query = { groupId: reference.group.groupId, inviteId: reference.inviteId }; validateGroupInviteQuery(query);
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(reference.group, 'group.invite.revoke', scope); const relay = await this.#hosting(reference.group.relayId, scope); const operation: GroupOperation = { group: reference.group, method: 'group.invite.revoke', request: groupInviteQueryCodec.encode(query) };
            await this.#operations.save(operation, undefined, scope); this.#pulse?.pulse(); await this.#execute(relay, operation, false, scope);
        }, scope), signal);
    }
    #copyInvitation(value: GroupInvitation): GroupInvitation {
        const copy = { group: { ...value.group }, document: groupInviteCodec.decode(groupInviteCodec.encode(value.document)) }; validateGroupRef(copy.group); validateGroupInvite(copy.document, this.clock.nowSeconds());
        if (copy.group.groupId !== copy.document.groupId) throw new ProtocolError('invalid_binding', 'Invitation belongs to another group.'); return copy;
    }
    async #previewInvitation(invite: GroupInvitation, signal: AbortSignal): Promise<{ state: GroupState }> {
        if (invite.document.invitee !== undefined && invite.document.invitee !== this.accountId) throw new ProtocolError('invalid_invitation', 'Invitation targets another account.');
        // Invitation-record reads require membership. The host checks its accepted invitation,
        // including remaining uses and revocation; this preview does not verify the supplied signature.
        const relay = await this.#hosting(invite.group.relayId, signal); const state = groupStateCodec.decode((await relay.requestHttp('GET', 'group.resolve', admission.groupResolveQueryCodec.encode({ groupId: invite.group.groupId, inviteId: invite.document.inviteId }), { signal }))!); validateGroupState(state);
        if (state.groupId !== invite.group.groupId) throw new ProtocolError('invalid_binding', 'Relay preview belongs to another group.'); return { state };
    }
    applyToGroup(source: GroupInvitation, signal?: AbortSignal): Promise<void> {
        const invite = this.#copyInvitation(source); const group = invite.group;
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(group, 'group.application.submit', scope); const preview = await this.#previewInvitation(invite, scope);
            if (preview.state.status !== 'active') throw new ProtocolError('invalid_invitation', 'Group is closed.');
            const relay = await this.#hosting(group.relayId, scope); const privateKey = this.#random.bytes(32);
            try {
                const member = await this.#secrets.prepareMemberState({ ...group, memberEncryptionPrivateKey: privateKey }, scope); privateKey.fill(0);
                const basis = { groupId: group.groupId, account: this.accountId, inviteId: invite.document.inviteId, memberEncryptionPublicKey: member.publicKey, deviceSignature: new Uint8Array(64) }; validateGroupApplication(basis);
                const application = await this.#signRequest(basis, groupApplicationCodec, scope); verifyGroupApplication(application, this.#device.certificate, this.context);
                if (member.deviceId !== certificateId(this.#device.certificate, this.context)) throw new GroupKeyAccessError('Local admission key protection device changed.');
                const operation: GroupOperation = { group, method: 'group.application.submit', request: groupApplicationCodec.encode(application) }; await this.#operations.save(operation, member, scope, preview.state); this.#pulse?.pulse();
                await this.#execute(relay, operation, false, scope);
            } finally { privateKey.fill(0); }
        }, scope), signal);
    }
    async #readApplications(group: GroupRef, page: GroupPageRequest, signal: AbortSignal): Promise<GroupPage<GroupApplicationInfo>> {
        const query = { ...page, groupId: group.groupId }; validateGroupListQuery(query); const relay = await this.#hosting(group.relayId, signal);
        const result = admission.groupApplicationPageCodec.decode((await relay.requestHttp('GET', 'group.application.list', groupListQueryCodec.encode(query), { signal }))!); admission.validateGroupApplicationPage(result, query, this.context);
        return { items: result.applications.map(value => ({ ...value, group })), ...(result.next === undefined ? {} : { nextCursor: result.next }) };
    }
    getApplications(group: GroupRef, page: GroupPageRequest = {}, signal?: AbortSignal): Promise<GroupPage<GroupApplicationInfo>> {
        group = { ...group }; validateGroupRef(group); const copy = { ...page }; return this.runOperation(scope => this.#readApplications(group, copy, scope), signal);
    }
    async #readRecoveries(group: GroupRef, page: GroupPageRequest, signal: AbortSignal): Promise<GroupPage<GroupKeyRecoveryInfo>> {
        const query = { ...page, groupId: group.groupId }; validateGroupListQuery(query); const relay = await this.#hosting(group.relayId, signal);
        const result = admission.groupRecoveryPageCodec.decode((await relay.requestHttp('GET', 'group.member.recovery.list', groupListQueryCodec.encode(query), { signal }))!); admission.validateGroupRecoveryPage(result, query, this.context);
        return { items: result.requests.map(value => ({ ...value, group })), ...(result.next === undefined ? {} : { nextCursor: result.next }) };
    }
    getKeyRecoveryRequests(group: GroupRef, page: GroupPageRequest = {}, signal?: AbortSignal): Promise<GroupPage<GroupKeyRecoveryInfo>> {
        group = { ...group }; validateGroupRef(group); const copy = { ...page }; return this.runOperation(scope => this.#readRecoveries(group, copy, scope), signal);
    }
    requestKeyRecovery(group: GroupRef, signal?: AbortSignal): Promise<GroupKeyRecoveryInfo> {
        group = { ...group }; validateGroupRef(group);
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(group, 'group.member.recovery.submit', scope); const relay = await this.#hosting(group.relayId, scope); await this.#synchronize(relay, group, scope, false); await this.#writable(group, scope);
            const privateKey = this.#random.bytes(32);
            try {
                const member = await this.#secrets.prepareMemberState({ ...group, memberEncryptionPrivateKey: privateKey }, scope); privateKey.fill(0);
                const basis = { groupId: group.groupId, account: this.accountId, memberEncryptionPublicKey: member.publicKey, deviceSignature: new Uint8Array(64) }; validateGroupMemberRecoveryRequest(basis);
                const request = await this.#signRequest(basis, groupMemberRecoveryRequestCodec, scope); const signerCertificate = this.#device.certificate; verifyGroupMemberRecoveryRequest(request, signerCertificate, this.context);
                if (member.deviceId !== certificateId(signerCertificate, this.context)) throw new GroupKeyAccessError('Local recovery key protection device changed.');
                const operation: GroupOperation = { group, method: 'group.member.recovery.submit', request: groupMemberRecoveryRequestCodec.encode(request) }; await this.#operations.save(operation, member, scope); this.#pulse?.pulse();
                const result = admission.groupRecoverySubmitResultCodec.decode((await this.#execute(relay, operation, false, scope))!); return { group, request, signerCertificate, ...result };
            } finally { privateKey.fill(0); }
        }, scope), signal);
    }
    #rejectRequests(group: GroupRef, accounts: readonly string[], recovery: boolean, signal?: AbortSignal): Promise<void> {
        group = { ...group }; validateGroupRef(group); const request = { groupId: group.groupId, accounts: [...accounts] }; admission.validateGroupAccountsRequest(request); const method = recovery ? 'group.member.recovery.reject' : 'group.application.reject';
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(group, method, scope); const relay = await this.#hosting(group.relayId, scope); const operation: GroupOperation = { group, method, request: admission.groupAccountsRequestCodec.encode(request) };
            await this.#operations.save(operation, undefined, scope); this.#pulse?.pulse(); await this.#execute(relay, operation, false, scope);
        }, scope), signal);
    }
    rejectApplications(group: GroupRef, accounts: readonly string[], signal?: AbortSignal): Promise<void> { return this.#rejectRequests(group, accounts, false, signal); }
    rejectKeyRecovery(group: GroupRef, accounts: readonly string[], signal?: AbortSignal): Promise<void> { return this.#rejectRequests(group, accounts, true, signal); }
    withdrawKeyRecovery(group: GroupRef, signal?: AbortSignal): Promise<void> { return this.rejectKeyRecovery(group, [this.accountId], signal); }
    #approve(group: GroupRef, accounts: readonly string[], recovery: boolean, signal?: AbortSignal): Promise<void> {
        group = { ...group }; validateGroupRef(group); const copy = [...accounts]; admission.validateGroupAccountsRequest({ groupId: group.groupId, accounts: copy }); const method = recovery ? 'group.member.recovery.approve' : 'group.application.approve';
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(group, method, scope); const relay = await this.#hosting(group.relayId, scope); await this.#synchronize(relay, group, scope); const state = await this.#writable(group, scope);
            const selected = new Map<string, GroupMemberKey>(); const targets = new Set(copy); const seenAccounts = new Set<string>(); const cursors = new Set<string>(); let cursor: string | undefined;
            const invitations = new Map<string, { value: GroupInviteInfo; selected: number }>();
            do {
                const page: GroupPage<GroupKeyRecoveryInfo | GroupApplicationInfo> = recovery ? await this.#readRecoveries(group, cursor === undefined ? {} : { cursor }, scope) : await this.#readApplications(group, cursor === undefined ? {} : { cursor }, scope);
                for (const item of page.items) {
                    const request = 'request' in item ? item.request : item.application;
                    if (seenAccounts.has(request.account)) throw new ProtocolError('invalid_pagination', 'Request account repeats across group pages.'); seenAccounts.add(request.account);
                    if (!targets.has(request.account)) continue;
                    if ('request' in item) { if (item.expiresAt <= this.clock.nowSeconds()) throw new ProtocolError('expired_recovery', 'Selected member recovery request has expired.'); }
                    else {
                        let chosen = invitations.get(item.application.inviteId);
                        if (!chosen) { chosen = { value: await this.#readInvite({ group, inviteId: item.application.inviteId }, scope), selected: 0 }; invitations.set(item.application.inviteId, chosen); }
                        const invite = chosen.value.invite.document; if (invite.invitee !== undefined && invite.invitee !== request.account) throw new ProtocolError('invalid_invitation', 'Application invitation targets another account.');
                        chosen.selected++; const maximum = invite.invitee === undefined ? invite.maxUses : 1;
                        if (maximum !== undefined && chosen.value.uses + chosen.selected > maximum) throw new ProtocolError('invalid_invitation', 'Selected applications exceed the remaining invitation uses.');
                    }
                    selected.set(request.account, { account: request.account, memberEncryptionPublicKey: request.memberEncryptionPublicKey });
                }
                cursor = page.nextCursor; if (cursor !== undefined) { if (cursors.has(cursor)) throw new ProtocolError('invalid_pagination', 'Group request list repeated a continuation cursor.'); cursors.add(cursor); }
            } while (cursor !== undefined && selected.size < targets.size);
            if (selected.size !== targets.size) throw new StateConflictError('At least one selected group request is missing or changed during pagination.');
            const basis = { groupId: group.groupId, prevHash: state.managementHash, members: copy.map(account => selected.get(account)!), deviceSignature: new Uint8Array(64) };
            const payload = await this.#sign(recovery ? { kind: 'memberRecoveryApproval', value: basis } : { kind: 'applicationApproval', value: basis }, scope); this.#checkManagement(state, payload);
            const secret = await this.#secrets.readClientSecret(group, state.clientSecretCommitment, scope); if (!secret) throw new GroupKeyAccessError('Current client group secret is unavailable for approval.');
            let operation: GroupOperation;
            try {
                const boxes: Record<string, GroupSecretBox> = Object.create(null) as Record<string, GroupSecretBox>;
                for (const member of basis.members) boxes[member.account] = sealGroupClientSecret({ groupId: group.groupId, account: member.account, memberEncryptionPublicKey: member.memberEncryptionPublicKey, clientSecretCommitment: state.clientSecretCommitment }, secret, this.context, this.#random);
                const value = { approval: payload.value, clientSecretCommitment: state.clientSecretCommitment, clientSecretBoxes: boxes };
                const request = recovery ? admission.groupRecoveryApproveRequestCodec.encode(value) : admission.groupApplicationApproveRequestCodec.encode(value); operation = { group, method, request };
                await this.#operations.save(operation, undefined, scope); this.#pulse?.pulse();
            } finally { secret.fill(0); }
            await this.#execute(relay, operation, false, scope);
        }, scope), signal);
    }
    approveApplications(group: GroupRef, accounts: readonly string[], signal?: AbortSignal): Promise<void> { return this.#approve(group, accounts, false, signal); }
    approveKeyRecovery(group: GroupRef, accounts: readonly string[], signal?: AbortSignal): Promise<void> { return this.#approve(group, accounts, true, signal); }
    rotateSecret(group: GroupRef, options: { readonly rotateOwnerMemberKey?: boolean } = {}, signal?: AbortSignal): Promise<void> {
        group = { ...group }; validateGroupRef(group); const rotateOwnerMemberKey = options.rotateOwnerMemberKey ?? false;
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(group, 'group.secret.rotation.commit', scope); const relay = await this.#hosting(group.relayId, scope); await this.#synchronize(relay, group, scope); const state = await this.#writable(group, scope);
            if (state.state.owner !== this.accountId) throw new ProtocolError('forbidden', 'Only the current owner can rotate the client group secret.');
            const row = (await this.#store.read([groupKey(group)], scope)).sets[0]![0]!.value; const ownerSequence = row.ownerSequence ?? 0; requireSafeInteger(ownerSequence, 0);
            let rotation = await this.#secrets.rotation(group, scope);
            if (rotation && (rotation.baseCommitment !== state.clientSecretCommitment || rotation.ownerSequence !== ownerSequence || rotation.expiresAt !== undefined && rotation.expiresAt <= this.clock.nowSeconds())) { await this.#secrets.discardRotation(rotation, scope); rotation = undefined; }
            if (rotation && Boolean(rotation.ownerPublicKey) !== rotateOwnerMemberKey) throw new StateConflictError('Retry the original owner-key choice while this rotation is staged.');
            let secret: Uint8Array | undefined;
            try {
                if (rotation) secret = await this.#secrets.readRotationSecret(rotation, scope);
                else {
                    let member: PreparedGroupMemberKey | undefined;
                    if (rotateOwnerMemberKey) { const privateKey = this.#random.bytes(32); try { member = await this.#secrets.prepareMemberState({ ...group, memberEncryptionPrivateKey: privateKey }, scope); } finally { privateKey.fill(0); } }
                    secret = this.#random.bytes(32); rotation = await this.#secrets.beginRotation(group, state.clientSecretCommitment, secret, member, scope);
                }
                const members = [...state.members].sort((a, b) => a.account < b.account ? -1 : a.account > b.account ? 1 : 0);
                for (let offset = 0; offset < members.length; offset += 64) {
                    const batch = members.slice(offset, offset + 64); const boxes: Record<string, GroupSecretBox> = Object.create(null) as Record<string, GroupSecretBox>;
                    for (const member of batch) boxes[member.account] = sealGroupClientSecret({ groupId: group.groupId, account: member.account, memberEncryptionPublicKey: member.account === this.accountId && rotation.ownerPublicKey ? rotation.ownerPublicKey : member.memberEncryptionPublicKey, clientSecretCommitment: rotation.commitment }, secret, this.context, this.#random);
                    const request = { groupId: group.groupId, baseCommitment: rotation.baseCommitment, clientSecretCommitment: rotation.commitment, clientSecretBoxes: boxes }; validateGroupRotationPrepareRequest(request);
                    const result = groupRotationPrepareResultCodec.decode((await relay.requestHttp('PATCH', 'group.secret.rotation.prepare', groupRotationPrepareRequestCodec.encode(request), { signal: scope }))!); validateGroupRotationPrepareResult(result, this.clock.nowSeconds());
                    if (result.prepared < batch.length) throw new ProtocolError('invalid_preparation', 'Relay did not retain all submitted rotation boxes.'); rotation = await this.#secrets.acknowledgeRotation(rotation, result.expiresAt);
                }
                secret.fill(0);
                const payload = await this.#sign({ kind: 'secretRotation', value: { groupId: group.groupId, prevHash: state.managementHash, clientSecretCommitment: rotation.commitment, ...(rotation.ownerPublicKey ? { ownerEncryptionPublicKey: rotation.ownerPublicKey } : {}), deviceSignature: new Uint8Array(64) } }, scope); this.#checkManagement(state, payload);
                const operation: GroupOperation = { group, method: 'group.secret.rotation.commit', request: requireObject(groupManagementPayloadCodec.encode(payload)) }; await this.#operations.save(operation, undefined, scope); this.#pulse?.pulse(); await this.#execute(relay, operation, false, scope);
            } finally { secret?.fill(0); }
        }, scope), signal);
    }
    #messageInfo(group: GroupRef, record: JsonObject): GroupMessageInfo | undefined {
        if (!record.isMessage || !record.decryptedPayload) return undefined; const event = groupEventCodec.decode(record.event!); const envelope = groupMessageEnvelopeCodec.decode(event.payload); const sender = requireObject(record.message!);
        return { ...groupMessageCodec.decode(record.decryptedPayload), group, sequence: event.sequence, messageId: envelope.messageId, sender: String(sender.sender), senderDeviceId: event.signerDeviceId!, createdAt: envelope.createdAt, acceptedAt: event.acceptedAt };
    }
    #sendPayload(group: GroupRef, payload: JsonObject, signal?: AbortSignal): Promise<GroupMessageInfo | undefined> {
        group = { ...group }; validateGroupRef(group); validateGroupPayload(payload);
        return this.runOperation(scope => this.#writes.run(async () => {
            await this.#operations.ensureAvailable(group, 'group.message.send', scope); const relay = await this.#hosting(group.relayId, scope); await this.#synchronize(relay, group, scope);
            const state = await this.#writable(group, scope); const reply = payload.reply_to_seq; if (reply !== undefined && (typeof reply !== 'number' || reply > state.sequence)) throw new ProtocolError('invalid_reply', 'Group reply must refer to preceding verified history.');
            const secret = await this.#secrets.readApplicationSecret(group, state.epoch, scope); if (!secret) throw new GroupKeyAccessError('Current group epoch key is unavailable.');
            let request: JsonObject;
            try { request = groupMessageEnvelopeCodec.encode(await encryptGroupMessage({ context: this.context, signer: this.#device, groupId: group.groupId, epoch: state.epoch,
                messageId: createIdentifier('message', this.#random), createdAt: this.clock.nowSeconds(), applicationSecret: secret, payload, random: this.#random, signal: scope })); } finally { secret.fill(0); }
            const operation: GroupOperation = { group, method: 'group.message.send', request }; await this.#operations.save(operation, undefined, scope); this.#pulse?.pulse();
            const record = (await this.#execute(relay, operation, false, scope))!;
            if (canonicalJson(record.decryptedPayload!) !== canonicalJson(payload)) throw new ProtocolError('conflicting_message', 'Accepted group message differs from the submitted content.'); return this.#messageInfo(group, record);
        }, scope), signal);
    }
    async sendMessage(group: GroupRef, draft: GroupMessageDraft, signal?: AbortSignal): Promise<GroupMessageInfo> { return (await this.#sendPayload(group, groupMessageCodec.encode(draft), signal))!; }
    async setNickname(group: GroupRef, nickname: string | null, signal?: AbortSignal): Promise<void> { await this.#sendPayload(group, groupMemberNicknameUpdateCodec.encode({ nickname }), signal); }
    getGroups(filter: { readonly membership?: GroupMembershipState; readonly role?: GroupRole; readonly relayId?: string } = {}, signal?: AbortSignal): Promise<QueryReader<GroupInfo>> {
        const copy = { ...filter }; if (copy.relayId !== undefined) validateRelayId(copy.relayId);
        if (copy.membership !== undefined && !['unknown', 'notMember', 'pending', 'member', 'left', 'removed', 'banned'].includes(copy.membership) || copy.role !== undefined && !['owner', 'administrator', 'member'].includes(copy.role)) throw new TypeError('Invalid group membership or role filter.');
        return this.runOperation(async scope => {
            const rows = (await this.#store.read([{ collection: 'groups' }], scope)).sets[0]!;
            return snapshotReader(rows.map(row => this.#snapshot({ groupId: row.key, relayId: String(row.value.relayId) }, row.value)).filter((value): value is GroupInfo => !!value && (!copy.membership || copy.membership === value.membership) && (!copy.role || copy.role === value.role) && (!copy.relayId || copy.relayId === value.ref.relayId)));
        }, signal);
    }
    getMembers(group: GroupRef, filter: { readonly role?: GroupRole; readonly search?: string } = {}, signal?: AbortSignal): Promise<QueryReader<GroupMemberInfo>> {
        group = { ...group }; validateGroupRef(group); const copy = { ...filter }; if (copy.role !== undefined && !['owner', 'administrator', 'member'].includes(copy.role)) throw new TypeError('Invalid group role filter.');
        return this.runOperation(async scope => snapshotReader((await this.#repository.get(group, scope))?.members.filter(member => (!copy.role || copy.role === member.role) && (!copy.search || member.account.includes(copy.search) || member.nickname?.includes(copy.search))).map(member => ({ accountId: member.account, role: member.role, memberEncryptionPublicKey: member.memberEncryptionPublicKey, ...(member.nickname === undefined ? {} : { nickname: member.nickname }) })).sort((a, b) => ordinal(a.accountId, b.accountId)) ?? []), signal);
    }
    getBans(group: GroupRef, search?: string, signal?: AbortSignal): Promise<QueryReader<string>> { group = { ...group }; validateGroupRef(group); return this.runOperation(async scope => snapshotReader((await this.#repository.get(group, scope))?.bans.filter(account => !search || account.includes(search)).sort() ?? []), signal); }
    getMessages(filter: { readonly groupId?: string; readonly sender?: string } = {}, signal?: AbortSignal): Promise<QueryReader<GroupMessageInfo>> {
        const copy = { ...filter }; if (copy.groupId !== undefined) validateIdentifier('group', copy.groupId); if (copy.sender !== undefined) validateAccountId(copy.sender);
        return this.runOperation(async scope => {
            const snapshot = await this.#store.read([{ collection: 'groups' }, { collection: 'group_events', ...(copy.groupId ? { prefix: `${copy.groupId}|` } : {}) }], scope); const relays = new Map(snapshot.sets[0]!.map(row => [row.key, String(row.value.relayId)]));
            const messages = snapshot.sets[1]!.flatMap(row => { const groupId = row.key.split('|')[0]!; const relayId = relays.get(groupId); if (!relayId) throw new ProtocolError('invalid_storage', 'Stored group event has no hosting association.'); const message = this.#messageInfo({ groupId, relayId }, row.value); return message && (!copy.sender || copy.sender === message.sender) ? [message] : []; });
            return snapshotReader(messages.sort((a, b) => a.createdAt - b.createdAt || ordinal(a.group.groupId, b.group.groupId) || a.sequence - b.sequence));
        }, signal);
    }
    protected override async onStart(signal: AbortSignal): Promise<void> {
        void this.#device.certificate; this.#pulse = new AsyncPulse(); this.#detachMessages = this.#accountMessages.on('timelineChanged', () => this.#pulse?.pulse());
        await this.#accountSync.request(signal);
        this.#jobs = [this.#refresh(this.runtimeSignal), this.#poll(this.runtimeSignal)]; this.#pulse.pulse();
    }
    async #poll(signal: AbortSignal): Promise<void> { try { for (;;) { await this.clock.delay(30000, signal); this.#pulse?.pulse(); } } catch (error) { if (!signal.aborted) throw error; } }
    async #subscribe(client: RelayClient, groups: readonly string[], signal: AbortSignal): Promise<void> {
        if (!(await client.getDescriptor(signal)).endpoints.some(value => value.startsWith('wss://'))) return;
        const selected = groups.slice(0, (await client.getInfo(signal)).limits.maxGroupSubscriptions ?? 0); let observed = this.#subscriptions.get(client.relayId);
        if (observed?.client !== client) {
            if (observed) { for (const detach of observed.detach) detach(); await observed.subscription.dispose(); }
            const subscription = client.createSubscription('group.subscribe', { group_ids: [] }, { signal, onSubscribed: () => { this.#pulse?.pulse(); }, onError: error => this.notifyBackgroundError({ operation: 'subscribe_group', resource: client.relayId, error }) });
            observed = { client, subscription, selected: new Set(), detach: [] }; this.#subscriptions.set(client.relayId, observed); const owner = observed;
            owner.detach.push(client.on('notificationReceived', notification => {
                if (!['group.timeline.changed', 'group.application.changed', 'group.member.recovery.changed'].includes(notification.method) || !owner.selected.size) return;
                const value = groupChangedNotificationCodec.decode(notification.params!); validateGroupChangedNotification(value);
                if (notification.method === 'group.timeline.changed') validateGroupTimelineChangedNotification(groupTimelineChangedNotificationCodec.decode(notification.params!));
                if (!owner.selected.has(value.groupId)) return; const group = { groupId: value.groupId, relayId: client.relayId };
                if (notification.method === 'group.application.changed') this.#notify('applicationsChanged', group); else if (notification.method === 'group.member.recovery.changed') this.#notify('keyRecoveryChanged', group); else this.#pulse?.pulse();
            }), client.on('socketConnected', () => { this.#pulse?.pulse(); }), client.on('errorOccurred', error => this.notifyBackgroundError({ operation: 'connect_group', resource: client.relayId, error })));
        }
        observed.selected = new Set(selected); observed.subscription.update({ group_ids: selected });
    }
    async #refresh(signal: AbortSignal): Promise<void> {
        try { for (;;) { await this.#pulse!.wait(signal);
            try {
                await this.#writes.run(() => this.#accountSync.consume(signal), signal);
                await this.#writes.run(async () => { for (const operation of await this.#operations.all(signal)) {
                    try { await this.#execute(await this.#hosting(operation.group.relayId, signal), operation, true, signal); }
                    catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'recover_group_operation', resource: operation.group.groupId, error }); }
                } }, signal);
                const rows = (await this.#store.read([{ collection: 'groups' }], signal)).sets[0]!; const selected = new Map<string, GroupRef[]>();
                for (const row of rows) { const group = { groupId: row.key, relayId: String(row.value.relayId) }; const info = this.#snapshot(group, row.value);
                    try { await this.#writes.run(() => this.#localRecovery(group, signal), signal); } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'recover_group_local', resource: group.groupId, error }); }
                    if (info?.group.status === 'closed' || info && !['member', 'pending', 'unknown'].includes(info.membership)) continue;
                    const groups = selected.get(group.relayId) ?? []; groups.push(group); selected.set(group.relayId, groups);
                }
                for (const [id, observed] of this.#subscriptions) if (!selected.has(id)) { observed.selected.clear(); observed.subscription.update({ group_ids: [] }); }
                for (const [relayId, groups] of selected) {
                    try {
                        const relay = await this.#hosting(relayId, signal);
                        try { await this.#subscribe(relay, groups.filter(group => { const row = rows.find(value => value.key === group.groupId)!; return this.#snapshot(group, row.value)?.membership === 'member'; }).map(group => group.groupId), signal); }
                        catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'subscribe_group', resource: relayId, error }); }
                        for (const group of groups) { try { await this.#writes.run(() => this.#synchronize(relay, group, signal), signal); } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'synchronize_group', resource: group.groupId, error }); } }
                    } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'connect_group', resource: relayId, error }); }
                }
            } catch (error) { throwIfAborted(signal); this.notifyBackgroundError({ operation: 'refresh_groups', error }); }
        } } catch (error) { if (!signal.aborted) throw error; }
    }
    protected override async onStop(): Promise<void> { this.#detachMessages?.(); this.#detachMessages = undefined; try { await Promise.all(this.#jobs); } finally { for (const observed of this.#subscriptions.values()) for (const detach of observed.detach) detach(); await Promise.all([...this.#subscriptions.values()].map(observed => observed.subscription.dispose())); this.#subscriptions.clear(); this.#jobs = []; this.#pulse = undefined; } }
    protected override async onDispose(): Promise<void> { this.#events.clear(); }
}
