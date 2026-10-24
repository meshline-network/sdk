import { createAbortController } from '../runtime/abort.js';
import type { AccountManager } from '../components/account.js';
import type { DeviceManager } from '../components/device.js';
import type { ProfileManager } from '../components/profile.js';
import type { BackgroundFailure } from '../components/component.js';
import { DeviceAuthorizationError, SubmissionOutcomeError } from '../components/authorization-error.js';
import { AccountRouteError } from '../components/account-route-error.js';
import type { RandomSource } from '../crypto/primitives.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateRelayId } from '../identity/neo.js';
import { accountDeviceStateCodec, authorizedDevice, certificateId, deviceCertificateCodec, validateDeviceState, type AccountDeviceState, type AccountRoute } from '../models/identity.js';
import { contentReferenceCodec } from '../models/content.js';
import { accountProfileCodec, validateProfile, type AccountProfile } from '../models/profile.js';
import type { NetworkContext } from '../protocol/context.js';
import { encodeBase64Url } from '../protocol/encoding.js';
import { canonicalJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { abortScope, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import type { MeshlineStore, RecordKey } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import type { RelayClientPool } from '../transport/pool.js';
import { RelayError } from '../transport/relay-error.js';
import { AccountEstablishmentError, type AccountEstablishmentOptions, type AccountInitialProfile } from './account-establishment.js';
import { validatePushGateway } from '../protocol/push-gateway.js';

export type { AccountEstablishmentOptions } from './account-establishment.js';
export interface AccountRecoveryOptions {
    readonly relayId?: string;
    readonly certificateValiditySeconds?: number;
    readonly routeValiditySeconds?: number;
    /** Used only when recovery creates a new device. Retained devices preserve their own declaration, including during renewal. */
    readonly pushGateway?: string;
    readonly previousDeviceState?: AccountDeviceState;
    readonly deviceStateRevision?: number;
    readonly routeRevision?: number;
}
export interface AccountOperationsOptions {
    readonly store: MeshlineStore; readonly pool: RelayClientPool; readonly account: AccountManager; readonly device: DeviceManager; readonly profile: ProfileManager;
    readonly context: NetworkContext; readonly accountId: string; readonly clock: RuntimeClock; readonly random: RandomSource;
    report(failure: BackgroundFailure): void;
    canChangeAuthorization(): boolean;
}
const establishmentKey: RecordKey = { collection: 'client_account_operations', key: 'establishment' };
const migrationKey: RecordKey = { collection: 'client_account_operations', key: 'migration' };
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
const day = 86400;

/** The client owns the next step; each manager owns its keys and signed publications. */
type EstablishmentRecord = JsonObject & {
    operationId: string;
    relayId: string;
    initialAccount: boolean;
    step: 'authorizingDevice' | 'ensuringRoute' | 'initializingProfile';
    certificateValiditySeconds: number;
    routeValiditySeconds: number;
    initialProfile?: JsonObject;
    initialPushGateway?: string;
    replaceDeviceId?: string;
    previousLocalDeviceId?: string;
};

function encodeInitialProfile(value: AccountInitialProfile | undefined): JsonObject | undefined {
    if (value === undefined) return undefined;
    for (const key of Object.keys(value)) if (!['nickname', 'bio', 'avatar', 'publicDiscovery'].includes(key)) throw new TypeError(`Unknown initial profile field ${key}.`);
    return { ...(value.nickname === undefined ? {} : { nickname: value.nickname }), ...(value.bio === undefined ? {} : { bio: value.bio }),
        ...(value.avatar === undefined ? {} : { avatar: contentReferenceCodec.encode(value.avatar) }), ...(value.publicDiscovery === undefined ? {} : { publicDiscovery: value.publicDiscovery }) };
}
function readInitialProfile(value: JsonObject | undefined): AccountInitialProfile | undefined {
    return value && { ...(value.nickname === undefined ? {} : { nickname: value.nickname as string }), ...(value.bio === undefined ? {} : { bio: value.bio as string }),
        ...(value.avatar === undefined ? {} : { avatar: contentReferenceCodec.decode(value.avatar) }), ...(value.publicDiscovery === undefined ? {} : { publicDiscovery: value.publicDiscovery as boolean }) };
}
function settings(source: AccountEstablishmentOptions) {
    if (source.pushGateway !== undefined) validatePushGateway(source.pushGateway);
    if (source.relayId !== undefined) validateRelayId(source.relayId);
    const certificateValiditySeconds = source.certificateValiditySeconds ?? 365 * day; const routeValiditySeconds = source.routeValiditySeconds ?? 365 * day;
    requireSafeInteger(certificateValiditySeconds, 1); requireSafeInteger(routeValiditySeconds, 1);
    if (certificateValiditySeconds > 720 * day || routeValiditySeconds > 3650 * day) throw new RangeError('Account certificate or route validity exceeds its supported limit.');
    return { ...source, certificateValiditySeconds, routeValiditySeconds };
}
function unavailable(error: unknown): boolean {
    if (error instanceof AggregateError) return error.errors.length > 0 && error.errors.every(unavailable);
    return error instanceof TypeError || error instanceof DOMException && ['TimeoutError', 'NetworkError'].includes(error.name)
        || error instanceof RelayError && ['temporarily_unavailable', 'bad_gateway', 'rate_limited', 'route_stale', 'target_not_local', 'route_not_found'].includes(error.code);
}
function lookupFailure(error: unknown): boolean {
    if (error instanceof AggregateError) return error.errors.length > 0 && error.errors.every(lookupFailure);
    return unavailable(error) || error instanceof RelayError || error instanceof ProtocolError && !['invalid_storage', 'invalid_binding', 'context_changed', 'not_initialized', 'disposed'].includes(error.code);
}

/** Called under the owning client's account-operation gate. Child publications retain their exact signed requests. */
export class ClientAccountOperations {
    constructor(readonly host: AccountOperationsOptions) {}
    async #select(requested: string | undefined, signal: AbortSignal): Promise<string> {
        const verify = async (id: string) => { validateRelayId(id); await (await this.host.pool.get(id, undefined, signal)).getDescriptor(signal); return id; };
        if (requested !== undefined) return verify(requested);
        const failures: unknown[] = []; const seen = new Set<string>();
        for await (const entry of this.host.pool.registry.getRelays(signal)) {
            throwIfAborted(signal); if (entry.status !== 'active' || seen.has(entry.relayId)) continue; seen.add(entry.relayId);
            try { return await verify(entry.relayId); }
            catch (error) { throwIfAborted(signal); if (!lookupFailure(error)) throw error; failures.push(error); this.host.report({ operation: 'select_relay', resource: entry.relayId, error }); }
        }
        throw new ProtocolError('no_active_relay', 'No verified active relay is available.', { cause: new AggregateError(failures) });
    }
    async #prepareDevice(validity: number, signal: AbortSignal, pushGateway?: string) {
        const local = this.host.device.local; const now = this.host.clock.nowSeconds();
        if (!local) return this.host.device.createDevice({ validitySeconds: validity, ...(pushGateway === undefined ? {} : { pushGateway }) }, signal);
        return local.notBefore <= now && now < local.expiresAt ? local : this.host.device.renewDevice(validity, signal);
    }
    async #confirm(relayId: string, signal: AbortSignal): Promise<void> {
        const local = this.host.device.local; if (!local) throw new ProtocolError('device_required', 'No local device has been created.');
        const state = await this.host.device.getOwnDeviceState(relayId, signal); if (!state) throw new ProtocolError('device_state_required', 'Home relay returned no device state after publication.');
        authorizedDevice(state, certificateId(local, this.host.context), this.host.context, this.host.clock.nowSeconds());
        await this.host.pool.get(relayId, { mode: 'device', signer: this.host.device }, signal);
    }
    async #clear(keys: readonly RecordKey[], signal: AbortSignal): Promise<void> {
        await updateStore(this.host.store, keys, snapshot => ({ mutations: keys.flatMap((key, index) => snapshot.sets[index]!.length ? [{ kind: 'delete' as const, ...key }] : []), result: undefined }), signal);
    }
    async establish(source: AccountEstablishmentOptions, signal: AbortSignal): Promise<void> {
        settings(source);
        if (source.replaceDeviceId !== undefined) validateIdentifier('device', source.replaceDeviceId);
        encodeInitialProfile(source.initialProfile);
        if (source.initialProfile) validateProfile({ account: this.host.accountId, publicDiscovery: false, updatedAt: 0, deviceSignature: new Uint8Array(64), ...source.initialProfile });
        try { await this.#establish(source, signal); }
        catch (error) {
            throwIfAborted(signal);
            if (error instanceof AccountEstablishmentError) throw error;
            if (error instanceof DeviceAuthorizationError) throw new AccountEstablishmentError(error.reason, error.message, error.details, { cause: error });
            if (error instanceof AccountRouteError) {
                const reasons = { signerRequired: 'accountSignerRequired', signatureDeclined: 'signatureDeclined', relayMismatch: 'relayMismatch', operationConflict: 'operationParametersConflict',
                    stateConflict: 'stateConflict', stateUnavailable: 'accountStateUnavailable', submissionOutcomeUnknown: 'submissionOutcomeUnknown' } as const;
                throw new AccountEstablishmentError(reasons[error.reason], error.message, {}, { cause: error });
            }
            if (error instanceof SubmissionOutcomeError) throw new AccountEstablishmentError('submissionOutcomeUnknown', error.message, {}, { cause: error });
            if (error instanceof StateConflictError || error instanceof RelayError && ['state_conflict', 'stale_state'].includes(error.code))
                throw new AccountEstablishmentError('stateConflict', 'Account state conflicts with the unfinished operation.', {}, { cause: error });
            if (error instanceof DOMException && error.name === 'NotAllowedError') throw new AccountEstablishmentError('signatureDeclined', 'Account authorization was declined.', {}, { cause: error });
            if (error instanceof ProtocolError && ['signer_required', 'integration_required'].includes(error.code)) throw new AccountEstablishmentError('accountSignerRequired', error.message, {}, { cause: error });
            if (lookupFailure(error)) throw new AccountEstablishmentError('accountStateUnavailable', 'Account state or authorization could not be obtained. Local data and pending work are preserved.', {}, { cause: error });
            throw error;
        }
    }
    async #checkpoint(previous: EstablishmentRecord | undefined, next: EstablishmentRecord, signal: AbortSignal): Promise<EstablishmentRecord> {
        await updateStore(this.host.store, [establishmentKey], snapshot => {
            if (fingerprint(snapshot.sets[0]![0]?.value) !== fingerprint(previous)) throw new StateConflictError('Account setup progress changed.');
            return { mutations: [{ kind: 'put', ...establishmentKey, value: next }], result: undefined };
        }, signal);
        return next;
    }
    #effective(relayId: string): boolean {
        const { device, account, clock, context } = this.host;
        const local = device.local; const state = device.deviceState;
        const certificate = local && state?.certificates.find(value => certificateId(value, context) === certificateId(local, context));
        return account.route?.relayId === relayId && account.route.expiresAt > clock.nowSeconds() && certificate !== undefined
            && certificate.notBefore <= clock.nowSeconds() && certificate.expiresAt > clock.nowSeconds()
            && deviceCertificateCodec.stringify(certificate) === deviceCertificateCodec.stringify(local!);
    }
    async #establish(source: AccountEstablishmentOptions, signal: AbortSignal): Promise<void> {
        const { account, device, store, clock } = this.host;
        const snapshot = await store.read([establishmentKey, migrationKey], signal);
        if (snapshot.sets[1]!.length) throw new AccountEstablishmentError('operationParametersConflict', 'Complete the explicit home-relay migration before establishing this device.');
        const saved = snapshot.sets[0]![0]?.value;
        let record: EstablishmentRecord | undefined;
        if (saved) {
            if (typeof saved.operationId !== 'string' || typeof saved.relayId !== 'string' || typeof saved.initialAccount !== 'boolean'
                || !['authorizingDevice', 'ensuringRoute', 'initializingProfile'].includes(String(saved.step)))
                throw new ProtocolError('invalid_storage', 'Invalid account establishment record.');
            record = saved as EstablishmentRecord;
            if (record.initialPushGateway !== undefined) validatePushGateway(record.initialPushGateway);
            if (source.pushGateway !== undefined && source.pushGateway !== record.initialPushGateway)
                throw new AccountEstablishmentError('operationParametersConflict', 'The initial push gateway is fixed by the unfinished operation.');
            settings({ relayId: record.relayId, certificateValiditySeconds: record.certificateValiditySeconds, routeValiditySeconds: record.routeValiditySeconds });
            if (source.initialProfile !== undefined && canonicalJson(encodeInitialProfile(source.initialProfile)!) !== canonicalJson(record.initialProfile ?? null))
                throw new AccountEstablishmentError('operationParametersConflict', 'The initial profile is fixed by the unfinished operation.');
        }
        const deviceIntent = () => {
            const replaceDeviceId = record ? record.replaceDeviceId : source.replaceDeviceId;
            return {
                ...(record ? { operationId: record.operationId } : {}),
                ...(typeof replaceDeviceId === 'string' ? { replaceDeviceId } : {}),
            };
        };

        // Inspect retained keys before discovery; reuse the result when checking relay authorization.
        let inspection = await device.inspectAuthorization({ ...(record ? { operationId: record.operationId } : {}) }, signal);
        const previousLocalDeviceId = inspection.previousLocalDeviceId;
        const refreshed = await account.refreshRoute(undefined, signal);
        const retained = account.route;
        if (source.relayId && (retained && retained.relayId !== source.relayId || record && record.relayId !== source.relayId))
            throw new AccountEstablishmentError('relayMismatch', 'Setup retains the original home relay.');
        if (!refreshed && retained && retained.expiresAt > clock.nowSeconds()) throw new AccountEstablishmentError('accountStateUnavailable', 'Discovery did not confirm the retained account route.');
        if (refreshed) inspection = await device.inspectAuthorization({ ...deviceIntent(), relayId: refreshed.relayId,
            ...(record && source.replaceDeviceId !== undefined ? { proposedReplaceDeviceId: source.replaceDeviceId } : {}) }, signal, inspection);
        const unconfirmed = !record && !refreshed ? await account.getUnconfirmedRoute(signal) : undefined;
        if (!record && inspection.authorized) return;
        if (!this.host.canChangeAuthorization()) throw new AccountEstablishmentError('operationParametersConflict', 'Stop synchronization before changing account authorization.');
        if (!refreshed && !retained && (record?.initialAccount === false || !record && device.deviceState))
            throw new AccountEstablishmentError('accountStateUnavailable', 'Existing device history has no original route. A not_found response cannot reconstruct it.');
        const relayId = retained?.relayId ?? record?.relayId ?? unconfirmed?.relayId ?? await this.#select(source.relayId, signal);
        if (record && record.relayId !== relayId || source.relayId && source.relayId !== relayId || unconfirmed && unconfirmed.relayId !== relayId)
            throw new AccountEstablishmentError('relayMismatch', 'The unfinished operation targets another home relay.');
        // Persist intent before execution. The device manager decides whether a replacement can change.
        if (!record) {
            const options = settings(source);
            if (unconfirmed && source.routeValiditySeconds !== undefined && source.routeValiditySeconds !== unconfirmed.expiresAt - unconfirmed.updatedAt)
                throw new AccountEstablishmentError('operationParametersConflict', 'Resume the unfinished route with its original lifetime.');
            record = await this.#checkpoint(undefined, { operationId: encodeBase64Url(this.host.random.bytes(16)), relayId, initialAccount: !retained && !refreshed,
                step: retained && (retained.expiresAt <= clock.nowSeconds() || unconfirmed) ? 'ensuringRoute' : 'authorizingDevice',
                certificateValiditySeconds: options.certificateValiditySeconds, routeValiditySeconds: unconfirmed ? unconfirmed.expiresAt - unconfirmed.updatedAt : options.routeValiditySeconds,
                ...(source.initialProfile ? { initialProfile: encodeInitialProfile(source.initialProfile)! } : {}),
                ...(source.pushGateway === undefined ? {} : { initialPushGateway: source.pushGateway }),
                ...(source.replaceDeviceId ? { replaceDeviceId: source.replaceDeviceId } : {}),
                ...(previousLocalDeviceId ? { previousLocalDeviceId } : {}) }, signal);
        } else {
            if (source.replaceDeviceId !== undefined && source.replaceDeviceId !== record.replaceDeviceId) {
                if (!refreshed) {
                    // An expired original route is renewed before the device owner evaluates a changed replacement.
                    if (retained) await account.ensureRoute(relayId, record.routeValiditySeconds, signal);
                    inspection = await device.inspectAuthorization({ ...deviceIntent(), ...(retained ? { relayId } : {}),
                        proposedReplaceDeviceId: source.replaceDeviceId }, signal, inspection);
                }
                record = await this.#checkpoint(record, { ...record, replaceDeviceId: source.replaceDeviceId, step: 'authorizingDevice' }, signal);
            }
            const routeExpired = account.route !== undefined && account.route.expiresAt <= clock.nowSeconds();
            if (routeExpired || record.step === 'initializingProfile' && !this.#effective(relayId))
                record = await this.#checkpoint(record, { ...record, step: routeExpired ? 'ensuringRoute' : 'authorizingDevice' }, signal);
        }
        // A step may be repeated after interruption; checkpoint only after its owner confirms it.
        for (;;) {
            if (record.step === 'ensuringRoute') {
                await account.ensureRoute(relayId, record.routeValiditySeconds, signal);
                record = await this.#checkpoint(record, { ...record, step: 'authorizingDevice' }, signal);
            } else if (record.step === 'authorizingDevice') {
                const result = await device.authorizeDevice({ operationId: record.operationId, relayId, validitySeconds: record.certificateValiditySeconds,
                    ...(record.initialPushGateway === undefined ? {} : { initialPushGateway: record.initialPushGateway }),
                    ...(typeof record.replaceDeviceId === 'string' ? { replaceDeviceId: record.replaceDeviceId } : {}),
                    ...(typeof record.previousLocalDeviceId === 'string' ? { previousLocalDeviceId: record.previousLocalDeviceId } : {}) }, signal);
                record = await this.#checkpoint(record, { ...record, step: result === 'awaitingRoute' ? 'ensuringRoute' : 'initializingProfile' }, signal);
            } else {
                const requireEffective = () => {
                    if (!this.#effective(relayId))
                        throw new AccountEstablishmentError('authorizationNotYetEffective', 'Account authorization expired or changed before setup completed.');
                };
                requireEffective();
                await this.host.profile.initializeAccountProfile(readInitialProfile(record.initialProfile), record.initialAccount, signal);
                requireEffective();
                const completed = record;
                await updateStore(store, [establishmentKey], snapshot => {
                    requireEffective();
                    if (fingerprint(snapshot.sets[0]![0]?.value) !== fingerprint(completed)) throw new StateConflictError('Account setup changed before completion.');
                    return { mutations: [{ kind: 'delete', ...establishmentKey }], result: undefined };
                }, signal);
                try { requireEffective(); }
                catch (error) {
                    await updateStore(store, [establishmentKey], snapshot => ({ mutations: snapshot.sets[0]!.length ? [] : [{ kind: 'put', ...establishmentKey, value: completed }], result: undefined }));
                    throw error;
                }
                return;
            }
        }
    }
    async recover(source: AccountRecoveryOptions, signal: AbortSignal): Promise<void> {
        const options = settings(source); const previous = source.previousDeviceState && accountDeviceStateCodec.decode(accountDeviceStateCodec.encode(source.previousDeviceState));
        if (previous) { validateDeviceState(previous, this.host.context); if (previous.account !== this.host.accountId) throw new ProtocolError('invalid_identity', 'Previous device state belongs to another account.'); }
        if (source.deviceStateRevision !== undefined) requireSafeInteger(source.deviceStateRevision, 0); if (source.routeRevision !== undefined) requireSafeInteger(source.routeRevision, 0);
        const { account, device } = this.host; let route: AccountRoute | undefined;
        try { route = await account.refreshRoute(undefined, signal); }
        catch (error) { throwIfAborted(signal); if (!lookupFailure(error)) throw error; this.host.report({ operation: 'resolve_recovery_route', error }); }
        let relayId: string;
        if (options.relayId === undefined && route) {
            try { relayId = await this.#select(route.relayId, signal); }
            catch (error) { throwIfAborted(signal); if (!lookupFailure(error)) throw error; this.host.report({ operation: 'select_recovery_relay', resource: route.relayId, error }); relayId = await this.#select(undefined, signal); }
        } else relayId = await this.#select(options.relayId, signal);
        if (account.route) {
            try { await device.getOwnDeviceState(account.route.relayId, signal); }
            catch (error) { throwIfAborted(signal); if (!lookupFailure(error)) throw error; this.host.report({ operation: 'resolve_recovery_devices', resource: account.route.relayId, error }); }
        }
        await this.#prepareDevice(options.certificateValiditySeconds, signal, options.pushGateway);
        await device.publishDeviceState(relayId, { ...(previous ? { previousState: previous } : {}), recovery: true, ...(source.deviceStateRevision === undefined ? {} : { revision: source.deviceStateRevision }), signal });
        await account.publishRoute(relayId, { validitySeconds: options.routeValiditySeconds, recovery: true, ...(source.routeRevision === undefined ? {} : { revision: source.routeRevision }), signal });
        await this.#confirm(relayId, signal); await this.#clear([establishmentKey, migrationKey], signal);
    }
    async #route(signal: AbortSignal): Promise<AccountRoute | undefined> {
        const previous = this.host.account.route;
        try { return await this.host.account.refreshRoute(undefined, signal) ?? previous; }
        catch (error) { throwIfAborted(signal); if (!previous || !unavailable(error)) throw error; this.host.report({ operation: 'resolve_migration_route', resource: previous.relayId, error }); return previous; }
    }
    async #deviceState(source: string, fallback: AccountDeviceState | undefined, signal: AbortSignal): Promise<AccountDeviceState> {
        try { await this.host.device.getOwnDeviceState(source, signal); }
        catch (error) { throwIfAborted(signal); if (!unavailable(error)) throw error; this.host.report({ operation: 'resolve_migration_devices', resource: source, error }); }
        const current = this.host.device.deviceState; const state = current && (!fallback || current.revision > fallback.revision) ? current : fallback;
        if (!state) throw new StateConflictError('The complete previous device state is unavailable. Recover explicitly rather than discarding other devices.');
        const local = this.host.device.local;
        if (!local || !state.certificates.some(value => certificateId(value, this.host.context) === certificateId(local, this.host.context))) throw new StateConflictError('Authorize the local device or recover explicitly before migrating.');
        return state;
    }
    async #profile(signal: AbortSignal): Promise<AccountProfile | undefined> {
        const previous = this.host.profile.profile;
        try { return await this.host.profile.getProfile(undefined, signal) ?? previous; }
        catch (error) { throwIfAborted(signal); if (!unavailable(error)) throw error; this.host.report({ operation: 'resolve_migration_profile', error }); return previous; }
    }
    #migration(value: JsonObject) {
        if (typeof value.source !== 'string' || typeof value.target !== 'string') throw new ProtocolError('invalid_storage', 'Stored migration has no source or target relay.');
        validateRelayId(value.source); validateRelayId(value.target); requireSafeInteger(value.routeValiditySeconds, 1);
        const state = accountDeviceStateCodec.decode(value.deviceState!); validateDeviceState(state, this.host.context);
        const profile = value.profile === undefined ? undefined : accountProfileCodec.decode(value.profile); if (profile) validateProfile(profile);
        if (state.account !== this.host.accountId || profile && profile.account !== this.host.accountId) throw new ProtocolError('invalid_storage', 'Migration snapshot belongs to another account.');
        return { source: value.source, target: value.target, state, profile, routeValiditySeconds: value.routeValiditySeconds };
    }
    async resumeMigration(signal: AbortSignal): Promise<void> { const value = (await this.host.store.read([migrationKey], signal)).sets[0]![0]?.value; if (value) await this.migrate(this.#migration(value).target, signal); }
    async migrate(relayId: string, signal: AbortSignal): Promise<void> {
        validateRelayId(relayId); const { store, account, device, profile, clock } = this.host;
        let value = (await store.read([migrationKey], signal)).sets[0]![0]?.value; let migration = value && this.#migration(value);
        if (migration && migration.target !== relayId) throw new StateConflictError('Resume migration to its original target or recover explicitly.');
        const route = await this.#route(signal); if (!route) throw new StateConflictError('No previous route is available. Establish or recover the account.');
        if (!migration && route.relayId === relayId && route.expiresAt > clock.nowSeconds()) return;
        await this.#select(relayId, signal);
        if (!migration) {
            const state = await this.#deviceState(route.relayId, undefined, signal); const previousProfile = await this.#profile(signal);
            if (account.route && account.route.relayId !== route.relayId) throw new StateConflictError('Account moved while migration was being prepared.');
            value = { source: route.relayId, target: relayId, deviceState: accountDeviceStateCodec.encode(state), routeValiditySeconds: route.expiresAt - route.updatedAt, ...(previousProfile ? { profile: accountProfileCodec.encode(previousProfile) } : {}) };
            const prepared = value;
            await updateStore(store, [migrationKey, { collection: 'account_routes', key: this.host.accountId }], snapshot => {
                if (snapshot.sets[0]!.length || snapshot.sets[1]![0]?.value.relay_id !== route.relayId) throw new StateConflictError('Account migration or route changed before staging.');
                return { mutations: [{ kind: 'put', ...migrationKey, value: prepared }], result: undefined };
            }, signal); migration = this.#migration(value);
        }
        if (route.relayId !== migration.source && route.relayId !== migration.target) throw new StateConflictError('Account moved to a third relay after this migration began. Resolve the concurrent change or recover explicitly.');
        if (route.relayId !== relayId || route.expiresAt <= clock.nowSeconds()) {
            const state = await this.#deviceState(migration.source, migration.state, signal); const local = device.local!;
            if (local.notBefore > clock.nowSeconds()) throw new StateConflictError('Local device certificate is not yet valid.');
            await this.#prepareDevice(local.expiresAt - local.notBefore, signal); const publication = await device.publishDeviceState(relayId, { previousState: state, signal });
            if (publication.status === 'staged' && publication.stagedUntil! - clock.nowSeconds() < 60) throw new StateConflictError('Staged device state must remain available for at least one minute.');
            if (account.route && account.route.relayId !== migration.source && account.route.relayId !== migration.target) throw new StateConflictError('Account moved to a third relay during migration preparation.');
            const deadline = createAbortController(); const window = abortScope([signal, deadline.signal]);
            const timer = publication.status === 'staged' ? clock.delay(60000, deadline.signal).then(() => deadline.abort(new DOMException('Migration route publication deadline expired.', 'TimeoutError')), error => { if (!deadline.signal.aborted) deadline.abort(error); }) : undefined;
            try { await account.publishRoute(relayId, { validitySeconds: migration.routeValiditySeconds, signal: window.signal }); }
            finally { deadline.abort(); window.dispose(); await timer; }
        }
        await this.#confirm(relayId, signal); await profile.publishProfile(migration.profile, signal);
        await updateStore(store, [migrationKey], snapshot => {
            if (fingerprint(snapshot.sets[0]![0]?.value) !== fingerprint(value)) throw new StateConflictError('Pending migration changed before completion.');
            return { mutations: [{ kind: 'delete', ...migrationKey }], result: undefined };
        }, signal);
    }
}
