import { createAbortController } from '../runtime/abort.js';
import type { AccountManager } from '../components/account.js';
import type { DeviceManager } from '../components/device.js';
import type { ProfileManager } from '../components/profile.js';
import type { BackgroundFailure } from '../components/component.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import { validateRelayId } from '../identity/neo.js';
import { accountDeviceStateCodec, authorizedDevice, certificateId, validateDeviceState, type AccountDeviceState, type AccountRoute } from '../models/identity.js';
import { accountProfileCodec, validateProfile, type AccountProfile } from '../models/profile.js';
import type { NetworkContext } from '../protocol/context.js';
import { canonicalJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { abortScope, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import type { MeshlineStore, RecordKey } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import type { RelayClientPool } from '../transport/pool.js';
import { RelayError } from '../transport/relay-error.js';

export interface AccountEstablishmentOptions { readonly relayId?: string; readonly certificateValiditySeconds?: number; readonly routeValiditySeconds?: number }
export interface AccountRecoveryOptions extends AccountEstablishmentOptions { readonly previousDeviceState?: AccountDeviceState; readonly deviceStateRevision?: number; readonly routeRevision?: number }
export interface AccountOperationsOptions {
    readonly store: MeshlineStore; readonly pool: RelayClientPool; readonly account: AccountManager; readonly device: DeviceManager; readonly profile: ProfileManager;
    readonly context: NetworkContext; readonly accountId: string; readonly clock: RuntimeClock;
    report(failure: BackgroundFailure): void;
}
const establishmentKey: RecordKey = { collection: 'client_account_operations', key: 'establishment' };
const migrationKey: RecordKey = { collection: 'client_account_operations', key: 'migration' };
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
const day = 86400;
function settings(source: AccountEstablishmentOptions) {
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
    async #prepareDevice(validity: number, signal: AbortSignal) {
        const local = this.host.device.local; const now = this.host.clock.nowSeconds();
        if (!local) return this.host.device.createDevice(validity, signal);
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
        const options = settings(source); const { account, device, store } = this.host;
        const route = await account.getRoute(undefined, signal);
        if (route) {
            if (device.local) {
                const state = await device.getDeviceState(undefined, signal);
                if (state && device.getAuthorizationState(certificateId(device.local, this.host.context)) === 'authorized') { await this.#clear([establishmentKey], signal); return; }
            }
            throw new StateConflictError('The account already has a route. Start its authorized local device or explicitly recover the account.');
        }
        if (account.route) throw new StateConflictError('A previous route is known. Use explicit account recovery instead of initial establishment.');
        const initial = (await store.read([establishmentKey], signal)).sets[0]![0]?.value;
        if (initial) { if (typeof initial.relayId !== 'string') throw new ProtocolError('invalid_storage', 'Stored establishment has no target relay.'); validateRelayId(initial.relayId); }
        if (initial && options.relayId !== undefined && initial.relayId !== options.relayId) throw new StateConflictError('Resume establishment at its original relay or recover explicitly.');
        const relayId = await this.#select(initial?.relayId as string | undefined ?? options.relayId, signal);
        if (!initial) await updateStore(store, [establishmentKey], snapshot => {
            if (snapshot.sets[0]!.length) throw new StateConflictError('Another establishment was staged concurrently.');
            return { mutations: [{ kind: 'put', ...establishmentKey, value: { relayId } }], result: undefined };
        }, signal);
        const certificate = await this.#prepareDevice(options.certificateValiditySeconds, signal);
        const publication = await device.publishDeviceState(relayId, { certificates: [certificate], signal });
        if (publication.status !== 'staged') throw new StateConflictError('Relay already considers this account established. Resolve its route before continuing.');
        await account.publishRoute(relayId, { validitySeconds: options.routeValiditySeconds, signal }); await this.#confirm(relayId, signal);
        await this.#clear([establishmentKey], signal);
    }
    async recover(source: AccountRecoveryOptions, signal: AbortSignal): Promise<void> {
        const options = settings(source); const previous = source.previousDeviceState && accountDeviceStateCodec.decode(accountDeviceStateCodec.encode(source.previousDeviceState));
        if (previous) { validateDeviceState(previous, this.host.context); if (previous.account !== this.host.accountId) throw new ProtocolError('invalid_identity', 'Previous device state belongs to another account.'); }
        if (source.deviceStateRevision !== undefined) requireSafeInteger(source.deviceStateRevision, 0); if (source.routeRevision !== undefined) requireSafeInteger(source.routeRevision, 0);
        const { account, device } = this.host; let route: AccountRoute | undefined;
        try { route = await account.getRoute(undefined, signal); }
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
        await this.#prepareDevice(options.certificateValiditySeconds, signal);
        await device.publishDeviceState(relayId, { ...(previous ? { previousState: previous } : {}), recovery: true, ...(source.deviceStateRevision === undefined ? {} : { revision: source.deviceStateRevision }), signal });
        await account.publishRoute(relayId, { validitySeconds: options.routeValiditySeconds, recovery: true, ...(source.routeRevision === undefined ? {} : { revision: source.routeRevision }), signal });
        await this.#confirm(relayId, signal); await this.#clear([establishmentKey, migrationKey], signal);
    }
    async #route(signal: AbortSignal): Promise<AccountRoute | undefined> {
        const previous = this.host.account.route;
        try { return await this.host.account.getRoute(undefined, signal) ?? previous; }
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
