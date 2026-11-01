import { agreeKey, devicePublicKey, encryptionPublicKey, requireLength, signDevice, verifyDevice, systemRandom, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError, SecretAccessError, StateConflictError } from '../errors.js';
import type { ContactAuthorizationIssue, DeviceSelectionInfo } from '../client/account-establishment.js';
import { DeviceAuthorizationError, SubmissionOutcomeError } from './authorization-error.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { matchesAccount, validateAccountId, validateRelayId } from '../identity/neo.js';
import type { AccountSigner, DeviceSigner, SecretProtector } from '../interactions.js';
import { deviceStatePublishResponseCodec, validateDeviceStatePublishResponse, type DeviceStatePublishResponse } from '../models/device-state.js';
import { contactAuthorizationCodec, contactConsentCodec, contactGrantCodec, contactGrantInput, contactRecordCodec, deviceStateQueryInput, signedDeviceStateQueryCodec, validateContactGrant, validateContactInvite, validateDeviceStateQuery, verifyContactGrant, verifyContactInvite, type ContactAuthorization, type ContactGrant, type SignedDeviceStateQuery } from '../models/contacts.js';
import {
    accountDeviceStateCodec, certificateAccountInput, certificateDeviceInput, certificateId, deviceCertificateCodec, deviceStateInput,
    validateCertificate, validateDeviceState, type AccountDeviceState, type DeviceCertificate,
} from '../models/identity.js';
import { nextRevision } from '../protocol/context.js';
import { decodeBase64Url, encodeBase64Url, equalBytes } from '../protocol/encoding.js';
import { canonicalJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { AsyncPulse } from '../runtime/async-pulse.js';
import { MetadataCache } from '../runtime/metadata-cache.js';
import { throwIfAborted } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import { finishSignedRequest, putSignedRequest, readSignedRequest, requestKey, type SignedRequest } from '../storage/signed-request.js';
import type { MeshlineStore, RecordKey, RecordQuery, StoredRecord, StoreMutation, StoreSnapshot } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import type { RelayClientPool } from '../transport/pool.js';
import type { RelayClient } from '../transport/client.js';
import { RelayError } from '../transport/relay-error.js';
import type { AccountManager } from './account.js';
import { ClientComponent, type ClientOptions } from './component.js';
import { FieldUpdate } from '../models/field-update.js';
import type { DeviceCreationOptions, DeviceRenewalOptions } from '../models/device-options.js';
import { validatePushGateway } from '../protocol/push-gateway.js';
import { StateUpdateCoordinator, type StateUpdateAttempt } from './state-update-coordinator.js';
import { canReuseRequest, isCurrentRequest, PublicationRequestState } from '../storage/state-publication.js';

export type DeviceAuthorizationState = 'unknown' | 'notRegistered' | 'notYetValid' | 'expired' | 'authorized';
export interface DeviceEvents { readonly deviceChanged: string; readonly deviceStateChanged: AccountDeviceState }
export interface DeviceManagerOptions extends ClientOptions {
    readonly store: MeshlineStore; readonly relayClients: RelayClientPool; readonly accountManager: AccountManager;
    readonly accountSigner?: AccountSigner; readonly secretProtector?: SecretProtector; readonly random?: RandomSource;
}
export interface PublishDeviceStateOptions {
    readonly certificates?: readonly DeviceCertificate[]; readonly previousState?: AccountDeviceState;
    readonly recovery?: boolean; readonly revision?: number; readonly signal?: AbortSignal;
}
export interface DeviceStatePublishResult extends DeviceStatePublishResponse { readonly deviceState: AccountDeviceState }
interface StateResolution { state: AccountDeviceState | undefined; changed: boolean }
/** @internal Result of local key inspection, optionally extended with fresh relay authorization. */
interface DeviceAuthorizationInspection { readonly previousLocalDeviceId: string | undefined; readonly authorized: boolean }
const method = 'device.state.publish';
const deviceLimit = 8;
const localKey: RecordKey = { collection: 'local_device', key: 'current' };
const bindingKey: RecordKey = { collection: 'identity_binding', key: 'device' };
const preparationKey: RecordKey = { collection: 'device_preparation', key: 'current' };
const stateKey = (account: string): RecordKey => ({ collection: 'device_states', key: account });
const clone = (value: DeviceCertificate): DeviceCertificate => deviceCertificateCodec.decode(deviceCertificateCodec.encode(value));
const copyState = (value: AccountDeviceState): AccountDeviceState => accountDeviceStateCodec.decode(accountDeviceStateCodec.encode(value));
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
function validity(seconds: number): void { requireSafeInteger(seconds, 1); if (seconds > 720 * 86400) throw new RangeError('Device validity cannot exceed 720 days.'); }

/** Protected local device keys and explicit account-authorized device-state publication. */
export class DeviceManager extends ClientComponent implements DeviceSigner {
    readonly relayClients: RelayClientPool;
    readonly #store: MeshlineStore; readonly #account: AccountManager; readonly #accountSigner: AccountSigner | undefined;
    readonly #protector: SecretProtector | undefined; readonly #random: RandomSource;
    readonly #keyGate = new AsyncGate(); readonly #publicationGate = new AsyncGate(); readonly #stateGate = new AsyncGate();
    readonly #events = new EventHub<DeviceEvents>();
    #local: DeviceCertificate | undefined; #state: AccountDeviceState | undefined;
    #signingKey: Uint8Array | undefined; #encryptionKey: Uint8Array | undefined;
    #homeClient: RelayClient | undefined; #homeListeners: (() => void)[] = [];
    #refresh: AsyncPulse | undefined;
    #homeRefresh: Promise<void> = Promise.resolve(); #removeRouteListener: (() => void) | undefined;
    #removeRelayListener: (() => void) | undefined;
    readonly #requiredRevisions = new Map<string, number>();
    readonly #stateQueries = new Map<string, { account: string; cache: MetadataCache<AccountDeviceState> }>();
    readonly #verifiedStates = new Map<string, AccountDeviceState>();
    readonly #stateRefreshedAt = new Map<string, number>();
    #stateStopping = false;
    readonly #updates: StateUpdateCoordinator<DeviceStatePublishResult>;
    readonly #mutationGate = new AsyncGate();

    constructor(options: DeviceManagerOptions) {
        super(options);
        for (const dependency of [options.relayClients, options.accountManager])
            if (dependency.accountId !== this.accountId || dependency.context.toString() !== this.context.toString())
                throw new ProtocolError('invalid_context', 'Device dependencies belong to another account or network.');
        this.#store = options.store; this.#account = options.accountManager; this.relayClients = options.relayClients;
        this.#accountSigner = options.accountSigner; this.#protector = options.secretProtector; this.#random = options.random ?? systemRandom;
        const owner = this;
        this.#updates = new class extends StateUpdateCoordinator<DeviceStatePublishResult> {
            protected readonly operation = method;
            protected readonly query = requestKey(method);
            protected key(): RecordKey { return requestKey(method); }
            protected resource(): string { return owner.accountId; }
            protected create(_resource: string, intent: JsonObject): JsonObject { return { relayId: intent.relayId ?? '', revision: -1 }; }
            protected publish(attempt: StateUpdateAttempt<DeviceStatePublishResult>, signal: AbortSignal): Promise<DeviceStatePublishResult> { return owner.#publishLatest(attempt, signal); }
            protected report(resource: string, error: unknown): void { owner.notifyBackgroundError({ operation: 'synchronize', resource, error }); }
            protected override followUp(result: DeviceStatePublishResult): number | null { return result.status === 'staged' ? this.clock.nowSeconds() + 30 : null; }
            protected override beforeMerge(previous: JsonObject, patch: JsonObject): void {
                if ('certificates' in patch) for (const key of Object.keys(previous)) if (key.startsWith('remove:')) delete previous[key];
            }
        }(this.#store, this.clock);
    }
    get local(): DeviceCertificate | undefined { return this.#local && clone(this.#local); }
    get deviceState(): AccountDeviceState | undefined { return this.#state && copyState(this.#state); }
    get certificate(): DeviceCertificate { this.ensureInitialized(); if (!this.#local) throw new ProtocolError('device_required', 'No local device has been created.'); return clone(this.#local); }
    on<K extends keyof DeviceEvents>(event: K, listener: EventListener<DeviceEvents[K]>): () => void { return this.#events.on(event, listener); }
    #notify<K extends keyof DeviceEvents>(event: K, value: DeviceEvents[K]): void {
        this.#events.notify(event, value, error => this.notifyBackgroundError({ operation: 'observer', resource: event, error }));
    }
    protected override async onInitialize(signal: AbortSignal): Promise<void> {
        this.#account.ensureInitialized();
        await this.#store.initialize({ context: this.context.toString(), accountId: this.accountId }, signal);
        const snapshot = await this.#store.read([localKey, bindingKey, stateKey(this.accountId)], signal);
        const local = snapshot.sets[0]![0]?.value; const binding = snapshot.sets[1]![0]?.value;
        if (Boolean(local) !== Boolean(binding)) throw new ProtocolError('invalid_storage', 'Device binding and protected keys must exist together.');
        if (local) {
            const certificate = this.#readCertificate(local);
            if (binding!.deviceId !== local.deviceId) throw new ProtocolError('invalid_storage', 'Local device differs from the persisted device binding.');
            this.#local = certificate;
        }
        const state = snapshot.sets[2]![0]?.value;
        if (state) { const value = accountDeviceStateCodec.decode(state); validateDeviceState(value, this.context);
            if (value.account !== this.accountId) throw new ProtocolError('invalid_storage', 'Stored device state belongs to another account.'); this.#state = value; this.#cacheState(value); }
    }
    #readCertificate(record: JsonObject): DeviceCertificate {
        if (record.certificate === undefined || typeof record.deviceId !== 'string' || typeof record.protectedSigningKey !== 'string' || typeof record.protectedEncryptionKey !== 'string')
            throw new ProtocolError('invalid_storage', 'Malformed local device record.');
        const certificate = deviceCertificateCodec.decode(record.certificate); validateCertificate(certificate, this.context);
        if (certificate.account !== this.accountId || certificateId(certificate, this.context) !== record.deviceId)
            throw new ProtocolError('invalid_storage', 'Local device does not match its account and identifier.');
        return certificate;
    }
    #purpose(id: string, kind: 'signing' | 'encryption'): string { return `Meshline/device-${kind}/v1/${this.context}/${this.accountId}/${id}`; }
    async #protect(key: Uint8Array, id: string, kind: 'signing' | 'encryption', signal: AbortSignal): Promise<string> {
        if (!this.#protector) throw new ProtocolError('protector_required', 'A secret protector is required to persist local keys.');
        const input = key.slice();
        try {
            const protectedData = await this.#protector.protect(input, this.#purpose(id, kind), signal);
            throwIfAborted(signal);
            if (!(protectedData instanceof Uint8Array) || protectedData.length === 0) throw new ProtocolError('invalid_protection', 'Secret protector returned no protected data.');
            return encodeBase64Url(protectedData);
        } finally { input.fill(0); }
    }
    async #issue(signingKey: Uint8Array, encryptionKey: Uint8Array, seconds: number, pushGateway: string | undefined, signal: AbortSignal): Promise<DeviceCertificate> {
        const signer = this.#accountSigner;
        if (!signer) throw new ProtocolError('signer_required', 'An account signer is required to authorize a device.');
        const publicKey = signer.publicKey.slice();
        if (signer.accountId !== this.accountId || !matchesAccount(this.accountId, publicKey)) throw new ProtocolError('invalid_identity', 'Account signer belongs to another account.');
        const now = this.clock.nowSeconds(); requireSafeInteger(now, 0); requireSafeInteger(now + seconds, 0);
        let certificate: DeviceCertificate = { account: this.accountId, accountPublicKey: publicKey, signingPublicKey: devicePublicKey(signingKey), encryptionPublicKey: encryptionKey.slice(),
            notBefore: now, expiresAt: now + seconds, ...(pushGateway === undefined ? {} : { pushGateway }), deviceSignature: new Uint8Array(64), accountSignature: new Uint8Array(64) };
        certificate = { ...certificate, deviceSignature: signDevice(certificateDeviceInput(certificate, this.context), signingKey) };
        certificate = { ...certificate, accountSignature: (await signer.sign(certificateAccountInput(certificate, this.context), signal)).slice() };
        throwIfAborted(signal); validateCertificate(certificate, this.context); return certificate;
    }
    /** Create and store a local certificate without a gateway declaration. Publish device state to authorize it. */
    createDevice(validitySeconds: number, signal?: AbortSignal): Promise<DeviceCertificate>;
    /** Create a local certificate with an optional gateway and a default lifetime of 365 days. Publish device state to authorize it. */
    createDevice(options: DeviceCreationOptions, signal?: AbortSignal): Promise<DeviceCertificate>;
    async createDevice(options: number | DeviceCreationOptions, signal?: AbortSignal): Promise<DeviceCertificate> {
        const validitySeconds = typeof options === 'number' ? options : options.validitySeconds ?? 365 * 86400;
        const pushGateway = typeof options === 'number' ? undefined : options.pushGateway;
        validity(validitySeconds);
        if (pushGateway !== undefined) validatePushGateway(pushGateway);
        if (!this.#protector || !this.#accountSigner) throw new ProtocolError('integration_required', 'Creating a device requires an account signer and a secret protector.');
        const certificate = await this.runOperation(scope => this.#mutationGate.run(async () => {
            await this.#requireNoPendingPreparation(scope);
            const existing = await this.#store.read([localKey, bindingKey], scope);
            if (existing.sets.some(rows => rows.length)) throw new StateConflictError('A local device already exists in this database.');
            let signing: Uint8Array | undefined; let encryption: Uint8Array | undefined;
            try {
                signing = this.#random.bytes(32); encryption = this.#random.bytes(32);
                requireLength(signing, 32, 'Device signing key'); requireLength(encryption, 32, 'Device encryption key');
                const certificate = await this.#issue(signing, encryptionPublicKey(encryption), validitySeconds, pushGateway, scope);
                const id = certificateId(certificate, this.context);
                const protectedSigningKey = await this.#protect(signing, id, 'signing', scope);
                const protectedEncryptionKey = await this.#protect(encryption, id, 'encryption', scope);
                await updateStore(this.#store, [localKey, bindingKey], snapshot => {
                    if (snapshot.sets.some(rows => rows.length)) throw new StateConflictError('Another writer created a local device.');
                    return { mutations: [{ ...localKey, kind: 'put', value: { deviceId: id, certificate: deviceCertificateCodec.encode(certificate), protectedSigningKey, protectedEncryptionKey } },
                        { ...bindingKey, kind: 'put', value: { deviceId: id } }], result: undefined };
                }, scope);
                this.#clearKeys(); this.#signingKey = signing; this.#encryptionKey = encryption; signing = encryption = undefined;
                this.#local = certificate; return certificate;
            } finally { signing?.fill(0); encryption?.fill(0); }
        }, scope), signal);
        this.#notify('deviceChanged', certificateId(certificate, this.context)); return clone(certificate);
    }
    /** Renew the local certificate, retaining its keys, identity and gateway. Publish device state to confirm the new authorization. */
    renewDevice(validitySeconds: number, signal?: AbortSignal): Promise<DeviceCertificate>;
    /** Renew with a retain/set/delete gateway update and a default lifetime of 365 days. Persist before publishing and retire old device sessions. */
    renewDevice(options: DeviceRenewalOptions, signal?: AbortSignal): Promise<DeviceCertificate>;
    async renewDevice(options: number | DeviceRenewalOptions, signal?: AbortSignal): Promise<DeviceCertificate> {
        const validitySeconds = typeof options === 'number' ? options : options.validitySeconds ?? 365 * 86400;
        const pushGateway = typeof options === 'number' ? undefined : options.pushGateway;
        validity(validitySeconds);
        if (pushGateway !== undefined && pushGateway !== FieldUpdate.delete) validatePushGateway(pushGateway);
        const certificate = await this.runOperation(scope => this.#mutationGate.run(async () => {
            await this.#requireNoPendingPreparation(scope, true);
            const saved = (await this.#store.read([localKey], scope)).sets[0]![0]?.value;
            if (!saved) throw new ProtocolError('device_required', 'No local device has been created.');
            const previous = this.#readCertificate(saved); const key = await this.#getKeyCopy('signing', scope);
            let issued: DeviceCertificate;
            try { issued = await this.#issue(key, previous.encryptionPublicKey, validitySeconds,
                pushGateway === FieldUpdate.delete ? undefined : pushGateway ?? previous.pushGateway, scope); }
            finally { key.fill(0); }
            await updateStore(this.#store, [localKey], snapshot => {
                if (fingerprint(snapshot.sets[0]![0]?.value) !== fingerprint(saved)) throw new StateConflictError('Local certificate changed while being renewed.');
                return { mutations: [{ ...localKey, kind: 'put', value: { ...saved, certificate: deviceCertificateCodec.encode(issued) } }], result: undefined };
            }, scope);
            this.#local = issued; return issued;
        }, scope), signal);
        await this.relayClients.rebindDevice(certificate);
        this.#notify('deviceChanged', certificateId(certificate, this.context)); return clone(certificate);
    }
    async #requireNoPendingPreparation(signal: AbortSignal, allowPending = false): Promise<void> {
        const snapshot = await this.#store.read([preparationKey, requestKey(method), { collection: 'client_account_operations', key: 'establishment' }], signal);
        allowPending &&= Boolean(this.#local && this.#state?.certificates.some(value => certificateId(value, this.context) === certificateId(this.#local!, this.context))) && !snapshot.sets[2]!.length;
        if (snapshot.sets[0]![0]?.value.operationId || !allowPending && readSignedRequest(snapshot.sets[1]![0]?.value)?.pending)
            throw new StateConflictError('Resume the unfinished device preparation or publication before standalone creation or renewal.');
    }
    async #getKey(kind: 'signing' | 'encryption', signal: AbortSignal): Promise<Uint8Array> {
        const cached = kind === 'signing' ? this.#signingKey : this.#encryptionKey; if (cached) return cached;
        if (!this.#protector) throw new ProtocolError('protector_required', 'A secret protector is required to access device keys.');
        const record = (await this.#store.read([localKey], signal)).sets[0]![0]?.value;
        if (!record) throw new ProtocolError('device_required', 'No local device has been created.');
        const certificate = this.#readCertificate(record);
        const key = await this.#protector.unprotect(decodeBase64Url(record[kind === 'signing' ? 'protectedSigningKey' : 'protectedEncryptionKey'] as string), this.#purpose(record.deviceId as string, kind), signal);
        try {
            throwIfAborted(signal); requireLength(key, 32, 'Unprotected device key');
            const derived = kind === 'signing' ? devicePublicKey(key) : encryptionPublicKey(key);
            if (!equalBytes(derived, kind === 'signing' ? certificate.signingPublicKey : certificate.encryptionPublicKey))
                throw new ProtocolError('invalid_key', 'Unprotected key does not match the local certificate.');
            if (kind === 'signing') this.#signingKey = key; else this.#encryptionKey = key;
            return key;
        } catch (error) { if (key instanceof Uint8Array) key.fill(0); throw error; }
    }
    async #getKeyCopy(kind: 'signing' | 'encryption', signal: AbortSignal): Promise<Uint8Array> {
        for (;;) {
            const selected = await this.#keyGate.run(async () => {
                const cached = kind === 'signing' ? this.#signingKey : this.#encryptionKey;
                if (cached) return { cached: cached.slice() };
                const record = (await this.#store.read([localKey], signal)).sets[0]![0]?.value;
                if (!record) throw new ProtocolError('device_required', 'No local device has been created.');
                return { record };
            }, signal);
            if (selected.cached) return selected.cached;
            if (!this.#protector) throw new ProtocolError('protector_required', 'A secret protector is required to access device keys.');
            const record = selected.record!; const certificate = this.#readCertificate(record);
            let key: Uint8Array | undefined = await this.#protector.unprotect(
                decodeBase64Url(record[kind === 'signing' ? 'protectedSigningKey' : 'protectedEncryptionKey'] as string), this.#purpose(String(record.deviceId), kind), signal);
            try {
                throwIfAborted(signal); requireLength(key, 32, 'Unprotected device key');
                const derived = kind === 'signing' ? devicePublicKey(key) : encryptionPublicKey(key);
                if (!equalBytes(derived, kind === 'signing' ? certificate.signingPublicKey : certificate.encryptionPublicKey)) throw new ProtocolError('invalid_key', 'Unprotected key does not match the local certificate.');
                const result = await this.#keyGate.run(async () => {
                    if (!this.#local || certificateId(this.#local, this.context) !== record.deviceId) return undefined;
                    let cached = kind === 'signing' ? this.#signingKey : this.#encryptionKey;
                    if (!cached) {
                        cached = key!;
                        if (kind === 'signing') this.#signingKey = cached; else this.#encryptionKey = cached;
                        key = undefined;
                    }
                    return cached.slice();
                }, signal);
                if (result) return result;
            } finally { key?.fill(0); }
        }
    }
    /** @internal Check keys before discovery, then reuse that inspection to verify relay state and replacement intent. */
    inspectAuthorization(options: { operationId?: string; relayId?: string; replaceDeviceId?: string; proposedReplaceDeviceId?: string }, signal: AbortSignal,
        inspection?: DeviceAuthorizationInspection): Promise<DeviceAuthorizationInspection> {
        return this.runOperation(async scope => {
            inspection ??= await this.#keyGate.run(async () => {
                const snapshot = await this.#store.read([localKey, preparationKey, bindingKey], scope);
                const local = snapshot.sets[0]![0]?.value; const draft = snapshot.sets[1]![0]?.value;
                if (Boolean(local) !== Boolean(snapshot.sets[2]!.length)) throw new ProtocolError('invalid_storage', 'Local device and its binding must exist together.');
                if (local) {
                    const certificate = this.#readCertificate(local);
                    const keys = await this.#readProtectedKeys(local, certificate, scope);
                    this.#clearKeys();
                    this.#signingKey = keys.signing;
                    this.#encryptionKey = keys.encryption;
                }
                if (draft?.operationId) {
                    if (typeof draft.operationId !== 'string' || draft.certificate === undefined) throw new ProtocolError('invalid_storage', 'Malformed device preparation.');
                    const certificate = deviceCertificateCodec.decode(draft.certificate);
                    const keys = await this.#readProtectedKeys(draft, certificate, scope);
                    keys.signing.fill(0); keys.encryption.fill(0);
                }
                let previousLocalDeviceId = local && String(local.deviceId);
                if (options.operationId && draft?.activatedOperationId === options.operationId)
                    previousLocalDeviceId = typeof draft.previousLocalDeviceId === 'string' ? draft.previousLocalDeviceId : undefined;
                return { previousLocalDeviceId, authorized: false };
            }, scope);
            if (!options.relayId && options.proposedReplaceDeviceId === undefined) return inspection;

            const current = options.relayId ? await this.#readAuthorizationState(options.relayId, scope) : undefined;
            if (options.relayId && !current) throw new DeviceAuthorizationError('accountStateUnavailable', 'The existing account has no readable complete device state.');
            const snapshot = await this.#store.read([requestKey(method), preparationKey], scope);
            const pending = readSignedRequest(snapshot.sets[0]![0]?.value)?.pending;
            const effective = this.#authorizationEffective(current, options.replaceDeviceId);
            if (options.proposedReplaceDeviceId !== undefined && options.proposedReplaceDeviceId !== options.replaceDeviceId && (pending || effective))
                throw new DeviceAuthorizationError('operationParametersConflict', 'Reconcile the original replacement before choosing another device.');
            const authorized = effective && !pending && !snapshot.sets[1]![0]?.value.operationId;
            if (authorized) await this.relayClients.get(options.relayId!, { mode: 'device', signer: this }, scope);
            return { ...inspection, authorized };
        }, signal);
    }
    async #readAuthorizationState(relayId: string, signal: AbortSignal): Promise<AccountDeviceState | undefined> {
        if (!this.#accountSigner && (!this.#local || this.#local.notBefore > this.clock.nowSeconds() || this.#local.expiresAt <= this.clock.nowSeconds()))
            throw new DeviceAuthorizationError('accountSignerRequired', 'An account signer is required to inspect authorization for this device.');
        try { return await this.getOwnDeviceState(relayId, signal); }
        catch (error) {
            throwIfAborted(signal);
            if (!this.#accountSigner && error instanceof RelayError && ['unauthorized', 'device_unknown'].includes(error.code))
                throw new DeviceAuthorizationError('accountSignerRequired', 'An account signer is required to inspect revoked device authorization.', {}, { cause: error });
            throw error;
        }
    }
    #authorizationEffective(state: AccountDeviceState | undefined, replaceDeviceId?: string): boolean {
        const certificate = this.#local;
        const registered = certificate && state?.certificates.find(value => certificateId(value, this.context) === certificateId(certificate, this.context));
        return registered !== undefined && registered.notBefore <= this.clock.nowSeconds() && registered.expiresAt > this.clock.nowSeconds()
            && deviceCertificateCodec.stringify(registered) === deviceCertificateCodec.stringify(certificate!)
            && (replaceDeviceId === undefined || !state!.certificates.some(value => certificateId(value, this.context) === replaceDeviceId));
    }
    async #readProtectedKeys(record: JsonObject, certificate: DeviceCertificate, signal: AbortSignal): Promise<{ signing: Uint8Array; encryption: Uint8Array }> {
        let signing: Uint8Array | undefined; let encryption: Uint8Array | undefined;
        try {
            if (!this.#protector) throw new SecretAccessError('unknown', 'A secret protector is required to read local keys.');
            const id = certificateId(certificate, this.context);
            signing = await this.#protector.unprotect(decodeBase64Url(record.protectedSigningKey as string), this.#purpose(id, 'signing'), signal);
            encryption = await this.#protector.unprotect(decodeBase64Url(record.protectedEncryptionKey as string), this.#purpose(id, 'encryption'), signal);
            throwIfAborted(signal); requireLength(signing, 32, 'Signing key'); requireLength(encryption, 32, 'Encryption key');
            if (!equalBytes(devicePublicKey(signing), certificate.signingPublicKey) || !equalBytes(encryptionPublicKey(encryption), certificate.encryptionPublicKey))
                throw new SecretAccessError('unknown', 'Protected keys do not match the retained certificate.');
            return { signing, encryption };
        } catch (error) {
            signing?.fill(0); encryption?.fill(0); throwIfAborted(signal);
            throw new DeviceAuthorizationError('localKeyAccessRequired', 'Restore access to the complete retained local data before retrying.',
                { keyIssue: error instanceof SecretAccessError ? error.failure : 'unknown' }, { cause: error });
        }
    }
    async #prepareAuthorizationDevice(operationId: string, seconds: number, initialPushGateway: string | undefined, signal: AbortSignal): Promise<DeviceCertificate> {
        const snapshot = await this.#store.read([localKey, preparationKey], signal);
        const local = snapshot.sets[0]![0]?.value;
        let draft = snapshot.sets[1]![0]?.value;
        if (draft?.operationId && draft.operationId !== operationId) throw new StateConflictError('Another device preparation must be resumed first.');
        if (!draft?.operationId && this.#local && this.#local.notBefore <= this.clock.nowSeconds() && this.#local.expiresAt > this.clock.nowSeconds()) return this.#local;

        const signer = this.#accountSigner;
        if (!signer) throw new DeviceAuthorizationError('accountSignerRequired', 'An account signer is required to authorize a device.');
        if (signer.accountId !== this.accountId || !matchesAccount(this.accountId, signer.publicKey)) throw new ProtocolError('invalid_identity', 'Account signer belongs to another account.');
        let keys: { signing: Uint8Array; encryption: Uint8Array } | undefined;
        try {
            let certificate: DeviceCertificate;
            if (draft?.operationId) {
                certificate = deviceCertificateCodec.decode(draft.certificate!);
                keys = await this.#readProtectedKeys(draft, certificate, signal);
            } else {
                keys = local ? { signing: (await this.#getKey('signing', signal)).slice(), encryption: (await this.#getKey('encryption', signal)).slice() }
                    : { signing: this.#random.bytes(32), encryption: this.#random.bytes(32) };
                certificate = local ? this.#readCertificate(local) : { account: this.accountId, accountPublicKey: signer.publicKey.slice(), signingPublicKey: devicePublicKey(keys.signing), encryptionPublicKey: encryptionPublicKey(keys.encryption),
                    notBefore: 0, expiresAt: 1, ...(initialPushGateway === undefined ? {} : { pushGateway: initialPushGateway }), deviceSignature: new Uint8Array(64), accountSignature: new Uint8Array(64) };
            }

            if (!draft?.operationId || certificate.expiresAt <= this.clock.nowSeconds()) {
                const now = this.clock.nowSeconds();
                certificate = { ...certificate, notBefore: now, expiresAt: now + seconds, accountSignature: new Uint8Array(64) };
                certificate = { ...certificate, deviceSignature: signDevice(certificateDeviceInput(certificate, this.context), keys.signing) };
                const id = certificateId(certificate, this.context);
                const next: JsonObject = { ...(draft?.activatedOperationId ? { activatedOperationId: draft.activatedOperationId } : {}), operationId,
                    ...(local ? { previousLocalDeviceId: local.deviceId! } : {}), certificate: deviceCertificateCodec.encode(certificate),
                    protectedSigningKey: local?.protectedSigningKey ?? draft?.protectedSigningKey ?? await this.#protect(keys.signing, id, 'signing', signal),
                    protectedEncryptionKey: local?.protectedEncryptionKey ?? draft?.protectedEncryptionKey ?? await this.#protect(keys.encryption, id, 'encryption', signal) };
                const previous = draft;
                await updateStore(this.#store, [localKey, preparationKey], current => {
                    if (fingerprint(current.sets[0]![0]?.value) !== fingerprint(local) || fingerprint(current.sets[1]![0]?.value) !== fingerprint(previous)) throw new StateConflictError('Device preparation changed.');
                    return { mutations: [{ kind: 'put', ...preparationKey, value: next }], result: undefined };
                }, signal);
                draft = next;
            }

            try { certificate = { ...certificate, accountSignature: (await signer.sign(certificateAccountInput(certificate, this.context), signal)).slice() }; }
            catch (error) {
                throwIfAborted(signal);
                if (error instanceof DOMException && error.name === 'NotAllowedError') throw new DeviceAuthorizationError('signatureDeclined', 'Account authorization was declined.', {}, { cause: error });
                throw error;
            }
            throwIfAborted(signal); validateCertificate(certificate, this.context);
            if (certificate.expiresAt <= this.clock.nowSeconds()) throw new DeviceAuthorizationError('authorizationNotYetEffective', 'Certificate expired while awaiting authorization. Retry with the retained keys.');

            const id = certificateId(certificate, this.context); const prepared = draft!;
            await this.#keyGate.run(async () => {
                await updateStore(this.#store, [localKey, preparationKey, bindingKey], current => {
                    if (fingerprint(current.sets[0]![0]?.value) !== fingerprint(local) || fingerprint(current.sets[1]![0]?.value) !== fingerprint(prepared)) throw new StateConflictError('Device preparation changed before activation.');
                    return { mutations: [
                        { kind: 'put', ...localKey, value: { deviceId: id, certificate: deviceCertificateCodec.encode(certificate), protectedSigningKey: prepared.protectedSigningKey!, protectedEncryptionKey: prepared.protectedEncryptionKey! } },
                        { kind: 'put', ...bindingKey, value: { deviceId: id } },
                        { kind: 'put', ...preparationKey, value: { activatedOperationId: operationId, ...(prepared.previousLocalDeviceId ? { previousLocalDeviceId: prepared.previousLocalDeviceId } : {}) } },
                    ], result: undefined };
                }, signal);
                this.#clearKeys();
                this.#signingKey = keys!.signing;
                this.#encryptionKey = keys!.encryption;
                keys = undefined;
                this.#local = certificate;
            }, signal);
            await this.relayClients.rebindDevice(certificate);
            this.#notify('deviceChanged', id);
            return certificate;
        } finally { keys?.signing.fill(0); keys?.encryption.fill(0); }
    }
    /** @internal Idempotent device authorization; the client alone checkpoints workflow steps. */
    authorizeDevice(options: { operationId: string; relayId: string; validitySeconds: number; initialPushGateway?: string; replaceDeviceId?: string; previousLocalDeviceId?: string }, signal: AbortSignal): Promise<'authorized' | 'awaitingRoute'> {
        validity(options.validitySeconds); validateRelayId(options.relayId);
        return this.runOperation(scope => this.#publicationGate.run(async () => {
            const operationKey = `${options.operationId}/${options.replaceDeviceId ?? ''}`;
            let conflicts = 0;
            for (;;) {
                const route = this.#account.route;
                if (route && (route.relayId !== options.relayId || route.expiresAt <= this.clock.nowSeconds()))
                    throw new DeviceAuthorizationError('accountStateUnavailable', 'Confirm the original home route before device authorization.');
                const current = route ? await this.#readAuthorizationState(options.relayId, scope) : undefined;
                if (route && !current) throw new DeviceAuthorizationError('accountStateUnavailable', 'The established account has no readable complete device state.');
                const pending = readSignedRequest((await this.#store.read([requestKey(method)], scope)).sets[0]![0]?.value);
                // An unresolved request retains its original certificate, even after expiry. Resolve it before renewal.
                const certificate = pending?.pending ? this.#local : await this.#prepareAuthorizationDevice(options.operationId, options.validitySeconds, options.initialPushGateway, scope);
                if (!certificate) throw new StateConflictError('The pending device publication has no retained local certificate.');

                const localId = certificateId(certificate, this.context);
                const selection: DeviceSelectionInfo = { deviceLimit, revision: current?.revision ?? 0,
                    ...(options.previousLocalDeviceId ? { previousLocalDeviceId: options.previousLocalDeviceId } : {}),
                    devices: (current?.certificates ?? []).map(value => ({ deviceId: certificateId(value, this.context), certificate: clone(value) })) };
                const registered = current?.certificates.find(value => certificateId(value, this.context) === localId);
                const replacing = options.replaceDeviceId && current?.certificates.some(value => certificateId(value, this.context) === options.replaceDeviceId);
                if (options.replaceDeviceId === localId || options.replaceDeviceId && !replacing && !registered)
                    throw new DeviceAuthorizationError('deviceSelectionRequired', 'Choose an existing other device from the current list.', { deviceSelection: selection });
                if (!pending?.pending && this.#authorizationEffective(current, options.replaceDeviceId)) {
                    await this.relayClients.get(options.relayId, { mode: 'device', signer: this }, scope);
                    return 'authorized';
                }

                let certificates = (current?.certificates ?? []).filter(value => certificateId(value, this.context) !== options.replaceDeviceId);
                certificates = registered ? certificates.map(value => certificateId(value, this.context) === localId ? certificate : value) : [...certificates, certificate];
                if (pending?.pending) {
                    if (!pending.document) throw new StateConflictError('A newer device intent is awaiting preparation.');
                    const request = accountDeviceStateCodec.decode(pending.document);
                    const contents = (values: readonly DeviceCertificate[]) => canonicalJson(values.map(value => deviceCertificateCodec.stringify(value)).sort());
                    // Compare the intended set independently of wire order. Rejected revisions remain consumed.
                    const matchesIntent = pending.relayId === options.relayId
                        && (pending.operationKey === undefined || pending.operationKey === operationKey)
                        && pending.revision > (current?.revision ?? -1)
                        && contents(request.certificates) === contents(certificates);
                    if (!matchesIntent)
                        throw new DeviceAuthorizationError('operationParametersConflict', 'A different device publication is unresolved. Reconcile its original intent before enabling this device.');
                    certificates = [...request.certificates];
                }
                if (certificates.length > deviceLimit) throw new DeviceAuthorizationError('deviceSelectionRequired', 'The device list is full; explicitly choose a device to replace.', { deviceSelection: selection });
                if (!this.#accountSigner) throw new DeviceAuthorizationError('accountSignerRequired', 'An account signer is required to authorize this device.');

                try {
                    // Preparation validates removal continuity on every attempt, including exact signed replays.
                    const signed = await this.#prepare(options.relayId, { certificates, ...(current ? { previousState: current } : {}) }, this.#accountSigner, scope, operationKey, selection);
                    const result = await this.#submit(signed, this.#accountSigner, scope, true);
                    if (result.changed) this.#notify('deviceStateChanged', copyState(result.result.deviceState));
                    if (result.result.status === 'staged') {
                        if (route) throw new DeviceAuthorizationError('authorizationNotYetEffective', 'The relay has only staged device authorization.');
                        return 'awaitingRoute';
                    }
                    if (!route) throw new DeviceAuthorizationError('stateConflict', 'The relay already considers the account established but no verified original route is available.');
                    if (certificate.expiresAt <= this.clock.nowSeconds()) {
                        if (pending?.pending) continue;
                        throw new DeviceAuthorizationError('authorizationNotYetEffective', 'Certificate expired during publication. Retry with the retained keys.');
                    }
                    await this.relayClients.invalidateDevice();
                    const confirmed = await this.#readAuthorizationState(options.relayId, scope);
                    if (!this.#authorizationEffective(confirmed, options.replaceDeviceId))
                        throw new DeviceAuthorizationError('authorizationNotYetEffective', 'The relay has not confirmed the requested local authorization and replacement.');
                    await this.relayClients.get(options.relayId, { mode: 'device', signer: this }, scope);
                    return 'authorized';
                } catch (error) {
                    if (route && error instanceof RelayError && ['state_conflict', 'stale_state'].includes(error.code) && conflicts++ < 2) continue;
                    throw error;
                }
            }
        }, scope), signal);
    }
    sign(input: Uint8Array, signal?: AbortSignal): Promise<Uint8Array> {
        const copy = input.slice();
        return this.runOperation(async scope => { const key = await this.#getKeyCopy('signing', scope); try { throwIfAborted(scope); return signDevice(copy, key); } finally { key.fill(0); } }, signal);
    }
    /** Caller owns and should erase the returned shared secret after deriving its purpose-specific key. */
    deriveSharedSecret(peerPublicKey: Uint8Array, signal?: AbortSignal): Promise<Uint8Array> {
        requireLength(peerPublicKey, 32, 'Peer X25519 key'); const copy = peerPublicKey.slice();
        return this.runOperation(async scope => { const key = await this.#getKeyCopy('encryption', scope); try { throwIfAborted(scope); return agreeKey(key, copy); } finally { key.fill(0); } }, signal);
    }
    getCertificate(deviceId: string): DeviceCertificate | undefined {
        this.ensureInitialized(); validateIdentifier('device', deviceId);
        const value = this.#state?.certificates.find(value => certificateId(value, this.context) === deviceId); return value && clone(value);
    }
    getAuthorizationState(deviceId: string): DeviceAuthorizationState {
        this.ensureInitialized(); validateIdentifier('device', deviceId);
        if (!this.#state) return 'unknown';
        const certificate = this.getCertificate(deviceId); if (!certificate) return 'notRegistered';
        const now = this.clock.nowSeconds(); return now < certificate.notBefore ? 'notYetValid' : now >= certificate.expiresAt ? 'expired' : 'authorized';
    }
    async #home(signal: AbortSignal): Promise<string> {
        const route = await this.#account.getRoute(undefined, signal);
        if (!route) throw new ProtocolError('route_required', 'Establish or recover the account route first.'); return route.relayId;
    }
    getDeviceState(target: string | ContactAuthorization = this.accountId, signal?: AbortSignal): Promise<AccountDeviceState | undefined> {
        return this.#readTarget(target, false, signal);
    }
    /** Awaits an update, retaining the one-minute failure cooldown. */
    refreshDeviceState(target: string | ContactAuthorization = this.accountId, signal?: AbortSignal): Promise<AccountDeviceState | undefined> {
        return this.#readTarget(target, true, signal);
    }
    async #readTarget(target: string | ContactAuthorization, force: boolean, signal?: AbortSignal): Promise<AccountDeviceState | undefined> {
        if (typeof target === 'string') return this.#getState(target, undefined, signal, undefined, force);
        const authorization = contactAuthorizationCodec.decode(contactAuthorizationCodec.encode(target));
        let accountId: string;
        if ('grantor' in authorization) {
            validateContactGrant(authorization, this.clock.nowSeconds()); accountId = authorization.grantor;
            if (authorization.grantee !== this.accountId) throw new ProtocolError('invalid_authorization', 'Contact grant must authorize this account.');
        } else { validateContactInvite(authorization, this.clock.nowSeconds()); accountId = authorization.inviter; }
        if (accountId === this.accountId) throw new ProtocolError('invalid_authorization', 'Signed device queries cannot target the current account.');
        return this.#getState(accountId, undefined, signal, authorization, force);
    }
    async getOwnDeviceState(relayId: string, signal?: AbortSignal): Promise<AccountDeviceState | undefined> {
        validateRelayId(relayId); return this.#getState(this.accountId, relayId, signal, undefined, true, true);
    }
    async #loadState(account: string, signal: AbortSignal): Promise<void> {
        const row = (await this.#store.read([stateKey(account)], signal)).sets[0]![0];
        if (!row) return;
        const state = accountDeviceStateCodec.decode(row.value);
        if (state.account !== account) throw new ProtocolError('invalid_storage', 'Stored device state belongs to another account.');
        if (state.revision > (this.#verifiedStates.get(account)?.revision ?? -1)) { validateDeviceState(state, this.context); this.#cacheState(state); }
    }
    #validateAuthorization(state: AccountDeviceState, authorization?: ContactAuthorization): void {
        if (!authorization) return;
        if ('grantor' in authorization) verifyContactGrant(authorization, state, this.context, this.clock.nowSeconds());
        else verifyContactInvite(authorization, state, this.context, this.clock.nowSeconds());
    }
    #cacheState(state: AccountDeviceState, completed?: number): void {
        if ((this.#verifiedStates.get(state.account)?.revision ?? -1) > state.revision) return;
        this.#verifiedStates.set(state.account, state);
        if (completed !== undefined) this.#stateRefreshedAt.set(state.account, completed);
        for (const entry of this.#stateQueries.values()) if (entry.account === state.account && (state.account === this.accountId || entry.cache.value)) {
            if (entry.cache.value?.revision !== state.revision) entry.cache.publish(state, completed);
            else if (completed !== undefined) entry.cache.recordSuccess(completed);
        }
    }
    async #getState(accountId: string, relayId: string | undefined, signal?: AbortSignal, authorization?: ContactAuthorization, force = false, afterCurrent = false): Promise<AccountDeviceState | undefined> {
        validateAccountId(accountId);
        return this.runOperation(async scope => {
            await this.#loadState(accountId, scope);
            const relay = relayId ?? await this.#home(scope);
            const useAccount = (afterCurrent || !this.#local) && accountId === this.accountId && this.#accountSigner !== undefined && (!this.#local || this.getAuthorizationState(certificateId(this.#local, this.context)) !== 'authorized');
            const key = JSON.stringify([accountId, relay, useAccount, this.#local && fingerprint(deviceCertificateCodec.encode(this.#local)), authorization && canonicalJson(contactAuthorizationCodec.encode(authorization))]);
            if (this.#stateStopping) throw new DOMException('Device state refreshes are stopping.', 'AbortError');
            let entry = this.#stateQueries.get(key);
            if (!entry) {
                const known = accountId === this.accountId || authorization ? this.#verifiedStates.get(accountId) : undefined;
                entry = { account: accountId, cache: new MetadataCache(this.clock, known, this.#stateRefreshedAt.get(accountId)) };
                this.#stateQueries.set(key, entry);
            }
            const cache = entry.cache;
            const usable = (state: AccountDeviceState): boolean => {
                try { this.#validateAuthorization(state, authorization); }
                catch (error) { if (error instanceof ProtocolError) return false; throw error; }
                return accountId !== this.accountId || !this.#local || state.certificates.some(device => certificateId(device, this.context) === certificateId(this.#local!, this.context) && device.notBefore <= this.clock.nowSeconds() && device.expiresAt > this.clock.nowSeconds());
            };
            if (afterCurrent) await cache.drain(scope);
            const result = await cache.read(usable, token => this.runOperation(async querySignal => {
                try {
                    const resolved = await this.#resolveState(accountId, relay, useAccount, querySignal, authorization, afterCurrent);
                    if (resolved.changed && resolved.state) this.#notify('deviceStateChanged', copyState(resolved.state));
                    if (resolved.state) {
                        const contact = accountId !== this.accountId ? (await this.#store.read([{ collection: 'contacts', key: accountId }], querySignal)).sets[0]![0]?.value : undefined;
                        const required = accountId === this.accountId ? this.#requiredRevisions.get(relay) : contact?.requiredDeviceRevision;
                        if (typeof required === 'number' && resolved.state.revision < required) throw new ProtocolError('stale_device_state', 'Device state has not reached the announced revision.');
                        this.#cacheState(resolved.state, this.clock.monotonicMilliseconds());
                    }
                    return resolved.state;
                } catch (error) {
                    if (accountId !== this.accountId && error instanceof RelayError && ['forbidden', 'unauthorized'].includes(error.code)) cache.invalidate();
                    throw error;
                }
            }, token), error => this.notifyBackgroundError({ operation: 'synchronize', resource: accountId, error }), scope, force);
            if (result) {
                try { this.#validateAuthorization(result, authorization); }
                catch (error) { cache.reject(error); throw error; }
            }
            return result && copyState(result);
        }, signal);
    }
    async #stopStateQueries(dispose = false): Promise<void> {
        this.#stateStopping = true;
        try { await Promise.all([...this.#stateQueries.values()].map(entry => entry.cache.stop(dispose))); }
        finally { this.#stateStopping = dispose; if (dispose) { this.#stateQueries.clear(); this.#verifiedStates.clear(); this.#stateRefreshedAt.clear(); } }
    }
    async #resolveState(accountId: string, relay: string, useAccount: boolean, scope: AbortSignal, authorization?: ContactAuthorization, allowAccount = false): Promise<StateResolution> {
        let parameters: JsonObject = { account: accountId };
        if (authorization) {
            let query: SignedDeviceStateQuery = { account: accountId, authorization, signerCertificate: this.certificate, createdAt: this.clock.nowSeconds(), deviceSignature: new Uint8Array(64) };
            query = { ...query, deviceSignature: await this.sign(deviceStateQueryInput(query, this.context), scope) };
            validateDeviceStateQuery(query, this.context, this.clock.nowSeconds()); parameters = signedDeviceStateQueryCodec.encode(query);
        }
        const read = async (account: boolean) => {
            const client = await this.relayClients.get(relay, account ? { mode: 'account', signer: this.#accountSigner! } : { mode: 'device', signer: this }, scope);
            return client.requestHttp(authorization ? 'POST' : 'GET', 'device.state.resolve', parameters, { signal: scope });
        };
        let response;
        try {
            try { response = await read(useAccount); }
            catch (error) {
                if (allowAccount && !useAccount && accountId === this.accountId && this.#accountSigner && error instanceof RelayError && ['unauthorized', 'device_unknown'].includes(error.code)) response = await read(true);
                else throw error;
            }
        } catch (error) { if (error instanceof RelayError && error.code === 'not_found') return { state: undefined, changed: false }; throw error; }
        if (response === undefined) throw new ProtocolError('invalid_response', 'Device resolution returned no state.');
        const state = accountDeviceStateCodec.decode(response); validateDeviceState(state, this.context);
        if (state.account !== accountId) throw new ProtocolError('invalid_identity', 'Resolved device state belongs to another account.');
        const changed = await this.#save(state, scope);
        // Verified revocation updates every scope even if it invalidates this query's proof.
        this.#validateAuthorization(state, authorization);
        return { state, changed };
    }
    /** Publishes device state; a complete certificate list replaces unfinished removals. */
    async publishDeviceState(relayId: string, options: PublishDeviceStateOptions = {}): Promise<DeviceStatePublishResult> {
        validateRelayId(relayId);
        options = { ...options, ...(options.certificates === undefined ? {} : { certificates: options.certificates.map(clone) }),
            ...(options.previousState === undefined ? {} : { previousState: copyState(options.previousState) }) };
        if (!this.#accountSigner) throw new ProtocolError('signer_required', 'An account signer is required to publish device state.');
        if (options.previousState) { validateDeviceState(options.previousState, this.context); if (options.previousState.account !== this.accountId) throw new ProtocolError('invalid_identity', 'Previous device state belongs to another account.'); }
        if (options.revision !== undefined) requireSafeInteger(options.revision, 0);
        if (options.certificates) {
            if (options.certificates.length > deviceLimit) throw new ProtocolError('invalid_device_state', 'A device state supports at most eight devices.');
            const ids = new Set<string>();
            for (const certificate of options.certificates) {
                validateCertificate(certificate, this.context);
                const id = certificateId(certificate, this.context);
                if (certificate.account !== this.accountId || ids.has(id)) throw new ProtocolError('invalid_device_state', 'Certificates must identify distinct devices belonging to this account.');
                ids.add(id);
            }
        }
        const before = this.#state?.revision;
        const result = await this.runOperation(signal => this.#updates.submit(this.accountId, this.#deviceIntent(relayId, options), signal), options.signal);
        if (before !== this.#state?.revision && this.#state) this.#notify('deviceStateChanged', copyState(this.#state));
        return { ...result, deviceState: copyState(result.deviceState) };
    }
    #deviceIntent(relayId: string, options: PublishDeviceStateOptions): JsonObject {
        return { relayId, revision: options.revision ?? null, recovery: options.recovery ?? false,
            previousState: options.previousState ? accountDeviceStateCodec.encode(options.previousState) : null,
            preserve: options.certificates === undefined,
            ...(options.certificates === undefined ? {} : { certificates: options.certificates.map(value => deviceCertificateCodec.encode(value)) }) };
    }
    async #importState(signal: AbortSignal): Promise<void> {
        const saved = readSignedRequest((await this.#store.read([requestKey(method)], signal)).sets[0]![0]?.value);
        if (saved?.pending && !saved.intent && saved.document) await this.#updates.import(this.accountId,
            this.#deviceIntent(saved.relayId, { certificates: accountDeviceStateCodec.decode(saved.document).certificates }), signal);
    }
    async #publishLatest(attempt: StateUpdateAttempt<DeviceStatePublishResult>, signal: AbortSignal): Promise<DeviceStatePublishResult> {
        const relayId = attempt.intent.relayId === null ? await this.#home(signal) : String(attempt.intent.relayId);
        const signer = this.#accountSigner;
        if (!signer) throw new ProtocolError('signer_required', 'An account signer is required to publish device state.');
        for (let retry = 0; ; retry++) {
            try {
                let certificates: readonly DeviceCertificate[] | undefined = Array.isArray(attempt.intent.certificates) ? attempt.intent.certificates.map(value => deviceCertificateCodec.decode(value)) : undefined;
                if (certificates && attempt.intent.preserve === true && this.#local) {
                    const local = this.#local; const id = certificateId(local, this.context);
                    certificates = certificates.map(value => certificateId(value, this.context) === id ? local : value);
                }
                let previousState = attempt.intent.previousState == null ? undefined : accountDeviceStateCodec.decode(attempt.intent.previousState);
                const removals = Object.keys(attempt.intent).filter(key => key.startsWith('remove:')).map(key => key.slice(7));
                if (removals.length) {
                    const current = await this.#readAuthorizationState(relayId, signal);
                    if (!current) throw new ProtocolError('device_state_required', 'The account has no published device state.');
                    previousState ??= current;
                    if (!certificates) {
                        const saved = readSignedRequest((await this.#store.read([requestKey(method)], signal)).sets[0]![0]?.value);
                        if ((!saved || saved.revision <= current.revision) && current.certificates.every(value => !removals.includes(certificateId(value, this.context))))
                            return { status: 'accepted', deviceState: current };
                        certificates = saved?.document && saved.pending && canReuseRequest(saved.requestState!) && saved.revision > current.revision
                            ? accountDeviceStateCodec.decode(saved.document).certificates : current.certificates;
                    }
                    certificates = certificates.filter(value => !removals.includes(certificateId(value, this.context)));
                }
                const options: PublishDeviceStateOptions = { recovery: attempt.intent.recovery === true,
                    ...(attempt.intent.revision == null ? {} : { revision: Number(attempt.intent.revision) }),
                    ...(certificates ? { certificates } : {}), ...(previousState ? { previousState } : {}) };
                const signed = await this.#prepare(relayId, options, signer, signal, undefined, undefined, attempt);
                const submitted = await this.#submit(signed, signer, signal, false, attempt);
                if (submitted.changed && attempt.background) attempt.deferNotification(() => this.#notify('deviceStateChanged', copyState(submitted.result.deviceState)));
                return submitted.result;
            } catch (error) {
                if (retry >= 2 || attempt.intent.revision != null || !(error instanceof RelayError) || !['stale_state', 'state_conflict'].includes(error.code)) throw error;
                await attempt.check(signal); await this.#readAuthorizationState(relayId, signal);
            }
        }
    }
    async #submit(signed: SignedRequest, signer: AccountSigner, signal: AbortSignal, classify = false, attempt?: StateUpdateAttempt<DeviceStatePublishResult>): Promise<{ result: DeviceStatePublishResult; changed: boolean }> {
        const state = accountDeviceStateCodec.decode(signed.document!); const client = await this.relayClients.get(signed.relayId, { mode: 'account', signer }, signal);
        let response;
        if (attempt) await attempt.check(signal);
        try { response = await client.requestHttp('PUT', method, signed.document!, { signal }); }
        catch (error) {
            if (error instanceof RelayError && error.isDefinitiveRejection) {
                try { if (attempt) await attempt.reject(signed.document!); else await finishSignedRequest(this.#store, method, signed); }
                catch (storageError) { throw new AggregateError([error, storageError], 'Device-state rejection could not be persisted.'); }
            }
            if (classify && !(error instanceof RelayError && error.isDefinitiveRejection) && !signal.aborted) throw new SubmissionOutcomeError('Device publication outcome is unknown.', { cause: error });
            throw error;
        }
        let receipt: DeviceStatePublishResponse;
        try {
            if (response === undefined) throw new ProtocolError('invalid_response', 'Device publication returned no result.');
            receipt = deviceStatePublishResponseCodec.decode(response); validateDeviceStatePublishResponse(receipt, this.clock.nowSeconds());
        } catch (error) {
            if (classify && !signal.aborted) throw new SubmissionOutcomeError('Device publication acknowledgement could not be verified.', { cause: error });
            throw error;
        }
        let changed = false;
        if (receipt.status === 'accepted') changed = await this.#save(state, signal, attempt, true);
        return { changed, result: { ...receipt, deviceState: state } };
    }
    /** Checks contact-grant continuity. Pending removals accumulate by device ID. */
    async removeDevice(deviceId: string, signal?: AbortSignal): Promise<void> {
        validateIdentifier('device', deviceId);
        const before = this.#state?.revision;
        const result = await this.runOperation(scope => this.#updates.submit(this.accountId,
            { relayId: null, ['remove:' + deviceId]: true, revision: null }, scope), signal);
        if (result.status !== 'accepted') throw new ProtocolError('removal_staged', 'Device removal was staged and is not yet authoritative.');
        if (before !== this.#state?.revision && this.#state) this.#notify('deviceStateChanged', copyState(this.#state));
    }
    #checkContinuity(deviceId: string, current: AccountDeviceState, certificates: readonly DeviceCertificate[], contacts: readonly StoredRecord[], requests: readonly StoredRecord[], binding: JsonObject | undefined, selection?: DeviceSelectionInfo): void {
        const now = this.clock.nowSeconds();
        const removed = current.certificates.find(value => certificateId(value, this.context) === deviceId);
        if (!removed || removed.expiresAt <= now) return;
        const issues: ContactAuthorizationIssue[] = [];
        const confirmedSignature = (issued: ContactGrant, confirmed: ContactGrant | undefined, accountId: string): boolean => {
            if (!confirmed || (confirmed.expiresAt ?? Infinity) <= now || confirmed.grantor !== this.accountId || confirmed.grantee !== accountId) return false;
            validateContactGrant(confirmed, now);
            const input = contactGrantInput(confirmed, this.context);
            return equalBytes(input, contactGrantInput(issued, this.context)) && certificates.some(certificate => certificate.notBefore <= now && now < certificate.expiresAt
                && confirmed.signatures[certificateId(certificate, this.context)] !== undefined
                && verifyDevice(input, confirmed.signatures[certificateId(certificate, this.context)]!, certificate.signingPublicKey));
        };
        if (binding?.deviceId !== deviceId) issues.push({ code: 'contact_history_unavailable' });
        else {
            const affected = new Set<string>();
            for (const row of contacts) {
                const contact = contactRecordCodec.decode(row.value.record!);
                if (contact.status !== 'active') continue;
                const confirmed = row.value.confirmedGrantTo === undefined ? undefined : contactGrantCodec.decode(row.value.confirmedGrantTo);
                for (const issued of [contact.grantToContact, confirmed]) if (issued && (issued.expiresAt ?? Infinity) > now && issued.signatures[deviceId]
                    && !confirmedSignature(issued, confirmed, contact.account)) affected.add(contact.account);
            }
            for (const row of requests) {
                if (row.value.direction !== 'outgoing' || ['failed', 'canceled'].includes(String(row.value.sendState))) continue;
                const grant = contactConsentCodec.decode(row.value.consent!).grant;
                if ((grant.expiresAt ?? Infinity) <= now || !grant.signatures[deviceId]) continue;
                if (row.value.sendState !== 'targetAccepted' || !confirmedSignature(grant, grant, String(row.value.accountId))) affected.add(String(row.value.accountId));
            }
            for (const accountId of [...affected].sort()) issues.push({ accountId, code: 'replacement_grant_not_confirmed' });
        }
        if (issues.length) {
            if (!selection) throw new ProtocolError('contact_grant_at_risk', 'Device removal cannot guarantee contact authorization continuity.');
            throw new DeviceAuthorizationError('contactContinuityUnverifiable', 'Complete contact authorization continuity before replacing this device.', { deviceSelection: selection, contactIssues: issues });
        }
    }
    async #prepare(relayId: string, options: PublishDeviceStateOptions, signer: AccountSigner, signal: AbortSignal, operationKey?: string, selection?: DeviceSelectionInfo, attempt?: StateUpdateAttempt<DeviceStatePublishResult>): Promise<SignedRequest> {
        const queries: RecordQuery[] = [stateKey(this.accountId), requestKey(method)];
        const snapshot = await this.#store.read(queries, signal); const known = snapshot.sets[0]![0]?.value; const saved = snapshot.sets[1]![0]?.value;
        const signed = readSignedRequest(saved); const knownState = known && accountDeviceStateCodec.decode(known);
        const knownRevision = Math.max(knownState?.revision ?? -1, signed?.revision ?? -1, options.previousState?.revision ?? -1);
        const refreshStaging = options.certificates === undefined && !options.recovery && options.revision === undefined;
        let certificates = options.certificates;
        if (certificates === undefined) {
            let previous = options.previousState;
            if (knownState && (!previous || knownState.revision > previous.revision)) previous = knownState;
            if (!attempt && refreshStaging && signed?.pending && signed.relayId !== relayId) throw new StateConflictError('Resolve pending device state before transferring it to another relay.');
            if (signed?.document && canReuseRequest(signed.requestState!) && signed.relayId === relayId && (!previous || signed.revision > previous.revision)) previous = accountDeviceStateCodec.decode(signed.document);
            if (!previous && !options.recovery) throw new StateConflictError('The complete device state is unavailable. Supply all certificates or recover explicitly.');
            certificates = previous?.certificates ?? [];
            if (this.#local) {
                const local = this.#local; const id = certificateId(local, this.context);
                const registered = certificates.some(value => certificateId(value, this.context) === id);
                if (!registered && !options.recovery) throw new StateConflictError('The local device is absent from the complete device state.');
                certificates = registered ? certificates.map(value => certificateId(value, this.context) === id ? local : value) : [...certificates, local];
            }
        }
        const previous = knownState && (!options.previousState || knownState.revision >= options.previousState.revision) ? knownState : options.previousState;
        const retainedIds = new Set(certificates.map(value => certificateId(value, this.context)));
        const removed = previous?.certificates.filter(value => !retainedIds.has(certificateId(value, this.context)) && value.expiresAt > this.clock.nowSeconds()) ?? [];
        const checkContinuity = (current: StoreSnapshot) => {
            for (const certificate of removed) this.#checkContinuity(certificateId(certificate, this.context), previous!, certificates,
                current.sets[2]!, current.sets[3]!, current.sets[4]![0]?.value, selection);
        };
        if (removed.length) {
            queries.push({ collection: 'contacts' }, { collection: 'contact_requests' }, bindingKey);
            checkContinuity(await this.#store.read(queries, signal));
        }
        const pending = signed?.pending && signed.document && canReuseRequest(signed.requestState!) ? accountDeviceStateCodec.decode(signed.document) : undefined;
        if (pending && signed!.relayId === relayId && (options.revision === undefined || options.revision === signed!.revision)
            && canonicalJson(pending.certificates.map(value => deviceCertificateCodec.encode(value))) === canonicalJson(certificates.map(value => deviceCertificateCodec.encode(value)))) {
            validateDeviceState(pending, this.context); if (pending.account !== this.accountId) throw new ProtocolError('invalid_storage', 'Pending device state belongs to another account.');
            if (attempt) await attempt.saveRequest(signed!.document!, { relayId, revision: signed!.revision }, signal);
            return { ...signed!, requestState: PublicationRequestState.Prepared };
        }
        if (!attempt && pending && !options.recovery && options.revision === undefined) throw new StateConflictError('Device publication has an unknown result. Retry the original certificates or resolve current state.');
        const publicKey = signer.publicKey.slice();
        if (signer.accountId !== this.accountId || !matchesAccount(this.accountId, publicKey)) throw new ProtocolError('invalid_identity', 'Account signer changed identity.');
        const revision = nextRevision(knownRevision, options.revision ?? (options.recovery ? Math.max(nextRevision(knownRevision), this.clock.nowSeconds() * 1000) : undefined));
        let state: AccountDeviceState = { account: this.accountId, accountPublicKey: publicKey, revision, certificates: certificates.map(clone), accountSignature: new Uint8Array(64) };
        state = { ...state, accountSignature: (await signer.sign(deviceStateInput(state, this.context), signal)).slice() }; validateDeviceState(state, this.context);
        const { operationKey: _previousOperation, ...publication } = signed ?? {};
        const prepared: SignedRequest = { ...(attempt ? publication : {}), relayId, revision, document: accountDeviceStateCodec.encode(state), pending: true, requestState: PublicationRequestState.Prepared, ...(operationKey ? { operationKey } : {}) };
        const save = () => updateStore(this.#store, queries, current => {
            if (fingerprint(current.sets[0]![0]?.value) !== fingerprint(known) || fingerprint(current.sets[1]![0]?.value) !== fingerprint(saved)) throw new StateConflictError('Device state changed while signing.');
            checkContinuity(current);
            return { mutations: [putSignedRequest(method, prepared)], result: undefined };
        }, signal);
        if (attempt) await attempt.current(save, signal); else await save();
        return prepared;
    }
    async #save(state: AccountDeviceState, signal: AbortSignal, attempt?: StateUpdateAttempt<DeviceStatePublishResult>, published = false): Promise<boolean> {
        let retirement: Promise<void> = Promise.resolve();
        const save = () => this.#stateGate.run(async () => {
            const document = accountDeviceStateCodec.encode(state); const json = canonicalJson(document);
            await updateStore(this.#store, [stateKey(state.account), requestKey(method)], snapshot => {
                const previous = snapshot.sets[0]![0]?.value;
                if (previous) {
                    const known = accountDeviceStateCodec.decode(previous);
                    if (state.revision < known.revision || state.revision === known.revision && json !== canonicalJson(previous)) throw new ProtocolError('stale_device_state', 'Device state is older or conflicts at the same revision.');
                }
                const mutations: StoreMutation[] = previous && canonicalJson(previous) === json ? [] : [{ ...stateKey(state.account), kind: 'put', value: document }];
                const signed = readSignedRequest(snapshot.sets[1]![0]?.value);
                if (state.account === this.accountId && signed?.pending && (!signed.intent || isCurrentRequest(signed.requestState!)) && (state.revision > signed.revision || state.revision === signed.revision && json === canonicalJson(signed.document ?? null))) {
                    const { intent: _intent, ...settled } = signed;
                    mutations.push(putSignedRequest(method, { ...settled, pending: false, nextAttemptAt: null }));
                }
                return { mutations, result: undefined };
            }, signal);
            this.#cacheState(state, published ? this.clock.monotonicMilliseconds() : undefined);
            if (state.account !== this.accountId) return false;
            const changed = this.#state?.revision !== state.revision; this.#state = copyState(state);
            if (this.#local && this.getAuthorizationState(certificateId(this.#local, this.context)) !== 'authorized') retirement = this.relayClients.invalidateDevice();
            return changed;
        }, signal);
        const changed = attempt ? await attempt.current(save, signal) : await save();
        await retirement; return changed;
    }
    #clearKeys(): void { this.#signingKey?.fill(0); this.#encryptionKey?.fill(0); this.#signingKey = this.#encryptionKey = undefined; }
    protected override async onStart(signal: AbortSignal): Promise<void> {
        if (!this.#local) throw new ProtocolError('device_required', 'Create and authorize a local device before starting.');
        const state = await this.getDeviceState(undefined, signal);
        if (!state || this.getAuthorizationState(certificateId(this.#local, this.context)) !== 'authorized')
            throw new ProtocolError('unauthorized_device', 'The local device has no current published authorization.');
        this.#refresh = new AsyncPulse();
        const runtime = this.runtimeSignal;
        this.#removeRouteListener = this.#account.on('accountChanged', () => { this.#refresh?.pulse(); });
        this.#removeRelayListener = this.relayClients.on('relayChanged', client => {
            if (this.#homeClient && client !== this.#homeClient && client.relayId === this.#homeClient.relayId
                && client.sessionMode === 'device' && client.state.authentication === 'device') this.#refresh?.pulse();
        });
        this.#homeRefresh = this.#refreshHome(runtime);
        // Retain terminal observer/clock failures until stop/dispose; never create unhandled rejections.
        this.#homeRefresh.catch(() => undefined);
        this.#refresh.pulse();
        await this.#importState(signal); this.#updates.start(this.runtimeSignal);
    }
    async #useHome(relayId: string, signal: AbortSignal): Promise<void> {
        const client = await this.relayClients.get(relayId, { mode: 'device', signer: this }, signal);
        if (client === this.#homeClient) return;
        const descriptor = await client.getDescriptor(signal);
        const previous = this.#homeClient;
        const listeners = [
            client.on('notificationReceived', notification => {
                if (this.#homeClient !== client || notification.method !== 'device.state.changed') return;
                const revision = notification.params?.revision; requireSafeInteger(revision, 0);
                this.#requiredRevisions.set(relayId, Math.max(revision, this.#requiredRevisions.get(relayId) ?? -1));
            }),
            client.on('errorOccurred', error => { if (this.#homeClient === client) this.notifyBackgroundError({ operation: 'connect', resource: relayId, error }); }),
        ];
        this.#homeClient = client;
        try { if (descriptor.endpoints.some(value => value.startsWith('wss://'))) client.startNotifications(); }
        catch (error) { this.#homeClient = previous; for (const remove of listeners) remove(); throw error; }
        for (const remove of this.#homeListeners) remove(); this.#homeListeners = listeners;
        if (previous && previous.relayId !== relayId) this.#requiredRevisions.delete(previous.relayId);
    }
    async #refreshHome(signal: AbortSignal): Promise<void> {
        try {
            for (;;) {
                await this.#refresh!.wait(signal);
                if (this.getAuthorizationState(certificateId(this.#local!, this.context)) !== 'authorized') continue;
                let relayId: string | undefined;
                try {
                    relayId = await this.#home(signal);
                    await this.#useHome(relayId, signal);
                } catch (error) {
                    throwIfAborted(signal);
                    this.notifyBackgroundError({ operation: 'connect', ...(relayId ? { resource: relayId } : {}), error });
                }
            }
        } catch (error) { if (!signal.aborted) throw error; }
    }
    protected override async onStop(): Promise<void> {
        try {
            this.#removeRouteListener?.(); this.#removeRouteListener = undefined;
            this.#removeRelayListener?.(); this.#removeRelayListener = undefined;
            const results = await Promise.allSettled([this.#updates.stop(), this.#homeRefresh, this.#stopStateQueries()]);
            const errors = results.filter((result): result is PromiseRejectedResult => result.status === 'rejected').map(result => result.reason);
            if (errors.length) throw new AggregateError(errors, 'Device background processing failed.');
        } finally {
            for (const remove of this.#homeListeners) remove(); this.#homeListeners = []; this.#homeClient = undefined;
            this.#refresh = undefined;
        }
    }
    protected override async onDispose(): Promise<void> {
        await this.#updates.drain(); await this.#stopStateQueries(true);
        try { if (this.#local) await this.relayClients.invalidateDevice(); }
        finally { this.#clearKeys(); this.#events.clear(); }
    }
}
