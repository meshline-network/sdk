import { agreeKey, devicePublicKey, encryptionPublicKey, requireLength, signDevice, verifyDevice, systemRandom, type RandomSource } from '../crypto/primitives.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { matchesAccount, validateAccountId, validateRelayId } from '../identity/neo.js';
import type { AccountSigner, DeviceSigner, SecretProtector } from '../interactions.js';
import { deviceStatePublishResponseCodec, validateDeviceStatePublishResponse, type DeviceStatePublishResponse } from '../models/device-state.js';
import { contactAuthorizationCodec, contactGrantCodec, contactGrantInput, contactRecordCodec, deviceStateQueryInput, signedDeviceStateQueryCodec, validateContactGrant, validateContactInvite, validateDeviceStateQuery, verifyContactGrant, verifyContactInvite, type ContactAuthorization, type SignedDeviceStateQuery } from '../models/contacts.js';
import {
    accountDeviceStateCodec, certificateAccountInput, certificateDeviceInput, certificateId, deviceCertificateCodec, deviceStateInput,
    validateCertificate, validateDeviceState, type AccountDeviceState, type DeviceCertificate,
} from '../models/identity.js';
import { nextRevision } from '../protocol/context.js';
import { decodeBase64Url, encodeBase64Url, equalBytes } from '../protocol/encoding.js';
import { canonicalJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { AsyncPulse } from '../runtime/async-pulse.js';
import { throwIfAborted } from '../runtime/clock.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import { finishSignedRequest, putSignedRequest, readSignedRequest, requestKey, type SignedRequest } from '../storage/signed-request.js';
import type { MeshlineStore, RecordKey, StoredRecord, StoreMutation } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import type { RelayClientPool } from '../transport/pool.js';
import type { RelayClient } from '../transport/client.js';
import { RelayError } from '../transport/relay-error.js';
import type { AccountManager } from './account.js';
import { ClientComponent, type ClientOptions } from './component.js';

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
const method = 'device.state.publish';
const localKey: RecordKey = { collection: 'local_device', key: 'current' };
const bindingKey: RecordKey = { collection: 'identity_binding', key: 'device' };
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
    #homeRefresh: Promise<void> = Promise.resolve(); #homePolling: Promise<void> = Promise.resolve();
    readonly #requiredRevisions = new Map<string, number>();

    constructor(options: DeviceManagerOptions) {
        super(options);
        for (const dependency of [options.relayClients, options.accountManager])
            if (dependency.accountId !== this.accountId || dependency.context.toString() !== this.context.toString())
                throw new ProtocolError('invalid_context', 'Device dependencies belong to another account or network.');
        this.#store = options.store; this.#account = options.accountManager; this.relayClients = options.relayClients;
        this.#accountSigner = options.accountSigner; this.#protector = options.secretProtector; this.#random = options.random ?? systemRandom;
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
            if (value.account !== this.accountId) throw new ProtocolError('invalid_storage', 'Stored device state belongs to another account.'); this.#state = value; }
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
    async #issue(signingKey: Uint8Array, encryptionKey: Uint8Array, seconds: number, signal: AbortSignal): Promise<DeviceCertificate> {
        const signer = this.#accountSigner;
        if (!signer) throw new ProtocolError('signer_required', 'An account signer is required to authorize a device.');
        const publicKey = signer.publicKey.slice();
        if (signer.accountId !== this.accountId || !matchesAccount(this.accountId, publicKey)) throw new ProtocolError('invalid_identity', 'Account signer belongs to another account.');
        const now = this.clock.nowSeconds(); requireSafeInteger(now, 0); requireSafeInteger(now + seconds, 0);
        let certificate: DeviceCertificate = { account: this.accountId, accountPublicKey: publicKey, signingPublicKey: devicePublicKey(signingKey), encryptionPublicKey: encryptionKey.slice(),
            notBefore: now, expiresAt: now + seconds, deviceSignature: new Uint8Array(64), accountSignature: new Uint8Array(64) };
        certificate = { ...certificate, deviceSignature: signDevice(certificateDeviceInput(certificate, this.context), signingKey) };
        certificate = { ...certificate, accountSignature: (await signer.sign(certificateAccountInput(certificate, this.context), signal)).slice() };
        throwIfAborted(signal); validateCertificate(certificate, this.context); return certificate;
    }
    async createDevice(validitySeconds: number, signal?: AbortSignal): Promise<DeviceCertificate> {
        validity(validitySeconds);
        if (!this.#protector || !this.#accountSigner) throw new ProtocolError('integration_required', 'Creating a device requires an account signer and a secret protector.');
        const certificate = await this.runOperation(scope => this.#keyGate.run(async () => {
            const existing = await this.#store.read([localKey, bindingKey], scope);
            if (existing.sets.some(rows => rows.length)) throw new StateConflictError('A local device already exists in this database.');
            let signing: Uint8Array | undefined; let encryption: Uint8Array | undefined;
            try {
                signing = this.#random.bytes(32); encryption = this.#random.bytes(32);
                requireLength(signing, 32, 'Device signing key'); requireLength(encryption, 32, 'Device encryption key');
                const certificate = await this.#issue(signing, encryptionPublicKey(encryption), validitySeconds, scope);
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
    async renewDevice(validitySeconds: number, signal?: AbortSignal): Promise<DeviceCertificate> {
        validity(validitySeconds);
        const certificate = await this.runOperation(scope => this.#keyGate.run(async () => {
            const saved = (await this.#store.read([localKey], scope)).sets[0]![0]?.value;
            if (!saved) throw new ProtocolError('device_required', 'No local device has been created.');
            const previous = this.#readCertificate(saved); const key = await this.#getKey('signing', scope);
            const issued = await this.#issue(key, previous.encryptionPublicKey, validitySeconds, scope);
            await updateStore(this.#store, [localKey], snapshot => {
                if (fingerprint(snapshot.sets[0]![0]?.value) !== fingerprint(saved)) throw new StateConflictError('Local certificate changed while being renewed.');
                return { mutations: [{ ...localKey, kind: 'put', value: { ...saved, certificate: deviceCertificateCodec.encode(issued) } }], result: undefined };
            }, scope);
            this.#local = issued; return issued;
        }, scope), signal);
        this.#notify('deviceChanged', certificateId(certificate, this.context)); return clone(certificate);
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
    sign(input: Uint8Array, signal?: AbortSignal): Promise<Uint8Array> {
        const copy = input.slice();
        return this.runOperation(scope => this.#keyGate.run(async () => { const key = await this.#getKey('signing', scope); throwIfAborted(scope); return signDevice(copy, key); }, scope), signal);
    }
    /** Caller owns and should erase the returned shared secret after deriving its purpose-specific key. */
    deriveSharedSecret(peerPublicKey: Uint8Array, signal?: AbortSignal): Promise<Uint8Array> {
        requireLength(peerPublicKey, 32, 'Peer X25519 key'); const copy = peerPublicKey.slice();
        return this.runOperation(scope => this.#keyGate.run(async () => { const key = await this.#getKey('encryption', scope); throwIfAborted(scope); return agreeKey(key, copy); }, scope), signal);
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
    async getDeviceState(target: string | ContactAuthorization = this.accountId, signal?: AbortSignal): Promise<AccountDeviceState | undefined> {
        if (typeof target === 'string') return this.#getState(target, undefined, signal);
        const authorization = contactAuthorizationCodec.decode(contactAuthorizationCodec.encode(target));
        let accountId: string;
        if ('grantor' in authorization) {
            validateContactGrant(authorization, this.clock.nowSeconds()); accountId = authorization.grantor;
            if (authorization.grantee !== this.accountId) throw new ProtocolError('invalid_authorization', 'Contact grant must authorize this account.');
        } else { validateContactInvite(authorization, this.clock.nowSeconds()); accountId = authorization.inviter; }
        if (accountId === this.accountId) throw new ProtocolError('invalid_authorization', 'Signed device queries cannot target the current account.');
        return this.#getState(accountId, undefined, signal, authorization);
    }
    async getOwnDeviceState(relayId: string, signal?: AbortSignal): Promise<AccountDeviceState | undefined> {
        validateRelayId(relayId); return this.#getState(this.accountId, relayId, signal);
    }
    async #getState(accountId: string, relayId: string | undefined, signal?: AbortSignal, authorization?: ContactAuthorization): Promise<AccountDeviceState | undefined> {
        validateAccountId(accountId); let changed = false;
        const state = await this.runOperation(async scope => {
            const relay = relayId ?? await this.#home(scope);
            const useAccount = accountId === this.accountId && this.#accountSigner !== undefined && (!this.#local || this.getAuthorizationState(certificateId(this.#local, this.context)) !== 'authorized');
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
                    if (!useAccount && accountId === this.accountId && this.#accountSigner && error instanceof RelayError && ['unauthorized', 'device_unknown'].includes(error.code)) response = await read(true);
                    else throw error;
                }
            } catch (error) { if (error instanceof RelayError && error.code === 'not_found') return undefined; throw error; }
            if (response === undefined) throw new ProtocolError('invalid_response', 'Device resolution returned no state.');
            const state = accountDeviceStateCodec.decode(response); validateDeviceState(state, this.context);
            if (state.account !== accountId) throw new ProtocolError('invalid_identity', 'Resolved device state belongs to another account.');
            if (authorization) {
                if ('grantor' in authorization) verifyContactGrant(authorization, state, this.context, this.clock.nowSeconds());
                else verifyContactInvite(authorization, state, this.context, this.clock.nowSeconds());
            }
            changed = await this.#save(state, scope); return state;
        }, signal);
        if (changed && state) this.#notify('deviceStateChanged', copyState(state));
        return state && copyState(state);
    }
    async publishDeviceState(relayId: string, options: PublishDeviceStateOptions = {}): Promise<DeviceStatePublishResult> {
        validateRelayId(relayId);
        options = { ...options, ...(options.certificates === undefined ? {} : { certificates: options.certificates.map(clone) }),
            ...(options.previousState === undefined ? {} : { previousState: copyState(options.previousState) }) };
        if (options.previousState) { validateDeviceState(options.previousState, this.context); if (options.previousState.account !== this.accountId) throw new ProtocolError('invalid_identity', 'Previous device state belongs to another account.'); }
        if (options.revision !== undefined) requireSafeInteger(options.revision, 0);
        let changed = false;
        const result = await this.runOperation(signal => this.#publicationGate.run(async () => {
            const signer = this.#accountSigner; if (!signer) throw new ProtocolError('signer_required', 'An account signer is required to publish device state.');
            const signed = await this.#prepare(relayId, options, signer, signal);
            const submitted = await this.#submit(signed, signer, signal); changed = submitted.changed; return submitted.result;
        }, signal), options.signal);
        if (changed) this.#notify('deviceStateChanged', copyState(result.deviceState)); return { ...result, deviceState: copyState(result.deviceState) };
    }
    async #submit(signed: SignedRequest, signer: AccountSigner, signal: AbortSignal): Promise<{ result: DeviceStatePublishResult; changed: boolean }> {
        const state = accountDeviceStateCodec.decode(signed.document); const client = await this.relayClients.get(signed.relayId, { mode: 'account', signer }, signal);
        let response;
        try { response = await client.requestHttp('PUT', method, signed.document, { signal }); }
        catch (error) {
            if (error instanceof RelayError && error.isDefinitiveRejection) {
                try { await finishSignedRequest(this.#store, method, signed); }
                catch (storageError) { throw new AggregateError([error, storageError], 'Device-state rejection could not be persisted.'); }
            }
            throw error;
        }
        if (response === undefined) throw new ProtocolError('invalid_response', 'Device publication returned no result.');
        const receipt = deviceStatePublishResponseCodec.decode(response); validateDeviceStatePublishResponse(receipt, this.clock.nowSeconds());
        let changed = false;
        if (receipt.status === 'accepted') changed = await this.#save(state, signal); else await finishSignedRequest(this.#store, method, signed);
        return { changed, result: { ...receipt, deviceState: state } };
    }
    /** Prevents removal of the last usable signature on a locally issued contact grant before replacement delivery acceptance. */
    async removeDevice(deviceId: string, signal?: AbortSignal): Promise<void> {
        validateIdentifier('device', deviceId);
        let changed: AccountDeviceState | undefined;
        await this.runOperation(async scope => {
            const relayId = await this.#home(scope); const current = await this.#getState(this.accountId, relayId, scope);
            if (!current) throw new ProtocolError('device_state_required', 'The account has no published device state.');
            const certificates = current.certificates.filter(value => certificateId(value, this.context) !== deviceId); if (certificates.length === current.certificates.length) return;
            await this.#publicationGate.run(async () => {
                const signer = this.#accountSigner; if (!signer) throw new ProtocolError('signer_required', 'An account signer is required to remove a device.');
                const signed = await this.#prepare(relayId, { certificates, previousState: current }, signer, scope, deviceId);
                const result = await this.#submit(signed, signer, scope);
                if (result.result.status !== 'accepted') throw new ProtocolError('removal_staged', 'Device removal was staged and is not yet authoritative.');
                if (result.changed) changed = result.result.deviceState;
            }, scope);
        }, signal);
        if (changed) this.#notify('deviceStateChanged', copyState(changed));
    }
    #checkRemoval(deviceId: string, certificates: readonly DeviceCertificate[], contacts: readonly StoredRecord[]): void {
        const now = this.clock.nowSeconds();
        for (const row of contacts) {
            const contact = contactRecordCodec.decode(row.value.record!); const issued = contact.grantToContact;
            if (contact.status !== 'active' || !issued || !issued.signatures[deviceId] || (issued.expiresAt ?? Infinity) <= now) continue;
            const confirmed = row.value.confirmedGrantTo === undefined ? undefined : contactGrantCodec.decode(row.value.confirmedGrantTo);
            if (confirmed && (confirmed.expiresAt ?? Infinity) <= now) continue;
            if (!confirmed) throw new ProtocolError('contact_grant_at_risk', 'Device removal would invalidate a contact grant before replacement signatures have been accepted for delivery.');
            validateContactGrant(confirmed, now);
            if (confirmed.grantor !== this.accountId || confirmed.grantee !== contact.account || contact.account !== row.key) throw new ProtocolError('invalid_storage', 'Confirmed contact grant has inconsistent account binding.');
            const input = contactGrantInput(confirmed, this.context);
            if (!certificates.some(certificate => certificate.notBefore <= now && now < certificate.expiresAt && confirmed.signatures[certificateId(certificate, this.context)] !== undefined
                && verifyDevice(input, confirmed.signatures[certificateId(certificate, this.context)]!, certificate.signingPublicKey)))
                throw new ProtocolError('contact_grant_at_risk', 'Device removal would invalidate a contact grant before replacement signatures have been accepted for delivery.');
        }
    }
    async #prepare(relayId: string, options: PublishDeviceStateOptions, signer: AccountSigner, signal: AbortSignal, removedDevice?: string): Promise<SignedRequest> {
        const queries = [stateKey(this.accountId), requestKey(method), ...(removedDevice ? [{ collection: 'contacts' }] : [])];
        const snapshot = await this.#store.read(queries, signal); const known = snapshot.sets[0]![0]?.value; const saved = snapshot.sets[1]![0]?.value;
        if (removedDevice) {
            if (!known || !options.previousState || canonicalJson(known) !== accountDeviceStateCodec.stringify(options.previousState)) throw new StateConflictError('Device state changed before preparing removal.');
            this.#checkRemoval(removedDevice, options.certificates!, snapshot.sets[2]!);
        }
        const signed = readSignedRequest(saved); const knownState = known && accountDeviceStateCodec.decode(known);
        const knownRevision = Math.max(knownState?.revision ?? -1, signed?.revision ?? -1, options.previousState?.revision ?? -1);
        const refreshStaging = options.certificates === undefined && !options.recovery && options.revision === undefined;
        let certificates = options.certificates;
        if (certificates === undefined) {
            let previous = options.previousState;
            if (knownState && (!previous || knownState.revision > previous.revision)) previous = knownState;
            if (refreshStaging && signed?.pending && signed.relayId !== relayId) throw new StateConflictError('Resolve pending device state before transferring it to another relay.');
            if (signed && signed.relayId === relayId && (!previous || signed.revision > previous.revision)) previous = accountDeviceStateCodec.decode(signed.document);
            if (!previous && !options.recovery) throw new StateConflictError('The complete device state is unavailable. Supply all certificates or recover explicitly.');
            certificates = previous?.certificates ?? [];
            if (this.#local) {
                const local = this.#local; const id = certificateId(local, this.context);
                const registered = certificates.some(value => certificateId(value, this.context) === id);
                if (!registered && !options.recovery) throw new StateConflictError('The local device is absent from the complete device state.');
                certificates = registered ? certificates.map(value => certificateId(value, this.context) === id ? local : value) : [...certificates, local];
            }
        }
        const pending = signed?.pending ? accountDeviceStateCodec.decode(signed.document) : undefined;
        if (!refreshStaging && pending && signed!.relayId === relayId && (options.revision === undefined || options.revision === signed!.revision)
            && canonicalJson(pending.certificates.map(value => deviceCertificateCodec.encode(value))) === canonicalJson(certificates.map(value => deviceCertificateCodec.encode(value)))) {
            validateDeviceState(pending, this.context); if (pending.account !== this.accountId) throw new ProtocolError('invalid_storage', 'Pending device state belongs to another account.'); return signed!;
        }
        if (pending && !refreshStaging && !options.recovery && options.revision === undefined) throw new StateConflictError('Device publication has an unknown result. Retry the original certificates or resolve current state.');
        const publicKey = signer.publicKey.slice();
        if (signer.accountId !== this.accountId || !matchesAccount(this.accountId, publicKey)) throw new ProtocolError('invalid_identity', 'Account signer changed identity.');
        const revision = nextRevision(knownRevision, options.revision ?? (options.recovery ? Math.max(nextRevision(knownRevision), this.clock.nowSeconds() * 1000) : undefined));
        let state: AccountDeviceState = { account: this.accountId, accountPublicKey: publicKey, revision, certificates: certificates.map(clone), accountSignature: new Uint8Array(64) };
        state = { ...state, accountSignature: (await signer.sign(deviceStateInput(state, this.context), signal)).slice() }; validateDeviceState(state, this.context);
        const prepared: SignedRequest = { relayId, revision, document: accountDeviceStateCodec.encode(state), pending: true };
        await updateStore(this.#store, queries, current => {
            if (fingerprint(current.sets[0]![0]?.value) !== fingerprint(known) || fingerprint(current.sets[1]![0]?.value) !== fingerprint(saved)) throw new StateConflictError('Device state changed while signing.');
            if (removedDevice && canonicalJson(current.sets[2]!.map(row => ({ key: row.key, value: row.value }))) !== canonicalJson(snapshot.sets[2]!.map(row => ({ key: row.key, value: row.value }))))
                throw new StateConflictError('Contact grants changed while signing device removal.');
            return { mutations: [putSignedRequest(method, prepared)], result: undefined };
        }, signal);
        return prepared;
    }
    async #save(state: AccountDeviceState, signal: AbortSignal): Promise<boolean> {
        return this.#stateGate.run(async () => {
            const document = accountDeviceStateCodec.encode(state); const json = canonicalJson(document);
            await updateStore(this.#store, [stateKey(state.account), requestKey(method)], snapshot => {
                const previous = snapshot.sets[0]![0]?.value;
                if (previous) {
                    const known = accountDeviceStateCodec.decode(previous);
                    if (state.revision < known.revision || state.revision === known.revision && json !== canonicalJson(previous)) throw new ProtocolError('stale_device_state', 'Device state is older or conflicts at the same revision.');
                }
                const mutations: StoreMutation[] = [{ ...stateKey(state.account), kind: 'put', value: document }];
                const signed = readSignedRequest(snapshot.sets[1]![0]?.value);
                if (state.account === this.accountId && signed?.pending && (state.revision > signed.revision || state.revision === signed.revision && json === canonicalJson(signed.document)))
                    mutations.push(putSignedRequest(method, { ...signed, pending: false }));
                return { mutations, result: undefined };
            }, signal);
            if (state.account !== this.accountId) return false;
            const changed = this.#state?.revision !== state.revision; this.#state = copyState(state);
            if (this.#local && this.getAuthorizationState(certificateId(this.#local, this.context)) !== 'authorized') await this.relayClients.invalidateDevice();
            return changed;
        }, signal);
    }
    #clearKeys(): void { this.#signingKey?.fill(0); this.#encryptionKey?.fill(0); this.#signingKey = this.#encryptionKey = undefined; }
    protected override async onStart(signal: AbortSignal): Promise<void> {
        if (!this.#local) throw new ProtocolError('device_required', 'Create and authorize a local device before starting.');
        const state = await this.#getState(this.accountId, undefined, signal);
        if (!state || this.getAuthorizationState(certificateId(this.#local, this.context)) !== 'authorized')
            throw new ProtocolError('unauthorized_device', 'The local device has no current published authorization.');
        this.#refresh = new AsyncPulse(); this.#requiredRevisions.clear();
        await this.#useHome(await this.#home(signal), signal);
        const runtime = this.runtimeSignal;
        this.#homeRefresh = this.#refreshHome(runtime); this.#homePolling = this.#pollHome(runtime);
        // Retain terminal observer/clock failures until stop/dispose; never create unhandled rejections.
        this.#homeRefresh.catch(() => undefined); this.#homePolling.catch(() => undefined);
        this.#refresh.pulse();
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
                this.#requiredRevisions.set(relayId, Math.max(revision, this.#requiredRevisions.get(relayId) ?? -1)); this.#refresh?.pulse();
            }),
            client.on('socketConnected', () => { if (this.#homeClient === client) this.#refresh?.pulse(); }),
            client.on('errorOccurred', error => { if (this.#homeClient === client) this.notifyBackgroundError({ operation: 'connect', resource: relayId, error }); }),
        ];
        this.#homeClient = client;
        try { if (descriptor.endpoints.some(value => value.startsWith('wss://'))) client.startNotifications(); }
        catch (error) { this.#homeClient = previous; for (const remove of listeners) remove(); throw error; }
        for (const remove of this.#homeListeners) remove(); this.#homeListeners = listeners;
        if (previous && previous.relayId !== relayId) this.#requiredRevisions.delete(previous.relayId);
    }
    async #pollHome(signal: AbortSignal): Promise<void> {
        try { for (;;) { await this.clock.delay(30_000, signal); this.#refresh?.pulse(); } }
        catch (error) { if (!signal.aborted) throw error; }
    }
    async #refreshHome(signal: AbortSignal): Promise<void> {
        try {
            for (;;) {
                await this.#refresh!.wait(signal);
                if (!this.#accountSigner && this.getAuthorizationState(certificateId(this.#local!, this.context)) !== 'authorized') continue;
                try {
                    const relayId = await this.#home(signal);
                    const state = await this.#getState(this.accountId, relayId, signal);
                    if (!state) throw new ProtocolError('device_state_required', 'The home relay has no published device state.');
                    if (this.getAuthorizationState(certificateId(this.#local!, this.context)) !== 'authorized')
                        throw new ProtocolError('unauthorized_device', 'The local device is no longer authorized.');
                    if (state.revision < (this.#requiredRevisions.get(relayId) ?? -1)) throw new ProtocolError('stale_device_state', 'Resolved device state is below the announced revision.');
                    await this.#useHome(relayId, signal);
                } catch (error) {
                    throwIfAborted(signal);
                    this.notifyBackgroundError({ operation: 'synchronize', ...(this.#homeClient ? { resource: this.#homeClient.relayId } : {}), error });
                    await this.clock.delay(5000, signal); this.#refresh?.pulse();
                }
            }
        } catch (error) { if (!signal.aborted) throw error; }
    }
    protected override async onStop(): Promise<void> {
        try {
            const results = await Promise.allSettled([this.#homeRefresh, this.#homePolling]);
            const errors = results.filter((result): result is PromiseRejectedResult => result.status === 'rejected').map(result => result.reason);
            if (errors.length) throw new AggregateError(errors, 'Device background processing failed.');
        } finally {
            for (const remove of this.#homeListeners) remove(); this.#homeListeners = []; this.#homeClient = undefined;
            this.#refresh = undefined; this.#requiredRevisions.clear();
        }
    }
    protected override async onDispose(): Promise<void> {
        try { if (this.#local) await this.relayClients.invalidateDevice(); }
        finally { this.#clearKeys(); this.#events.clear(); }
    }
}
