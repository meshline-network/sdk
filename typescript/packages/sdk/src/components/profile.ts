import { ProtocolError, StateConflictError } from '../errors.js';
import { validateAccountId } from '../identity/neo.js';
import type { DeviceSigner } from '../interactions.js';
import { contentReferenceCodec, type ContentReference } from '../models/content.js';
import { deviceCertificateCodec } from '../models/identity.js';
import { accountProfileCodec, profileInput, profileResolveResultCodec, validateProfile, validateProfileResult, type AccountProfile, type ProfileResolveResult } from '../models/profile.js';
import { canonicalJson, type JsonObject } from '../protocol/json.js';
import { MetadataCache } from '../runtime/metadata-cache.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import { finishSignedRequest, putSignedRequest, readSignedRequest, requestKey, type SignedRequest } from '../storage/signed-request.js';
import type { MeshlineStore, RecordKey, StoreMutation } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import type { RelayClientPool } from '../transport/pool.js';
import { RelayError } from '../transport/relay-error.js';
import type { AccountManager } from './account.js';
import { ClientComponent, type ClientOptions } from './component.js';
import type { AccountInitialProfile } from '../client/account-establishment.js';
import { SubmissionOutcomeError } from './authorization-error.js';

/** Omitted fields are retained; null removes an optional field, including an existing empty string. */
export interface ProfileUpdate { readonly nickname?: string | null; readonly bio?: string | null; readonly avatar?: ContentReference | null; readonly publicDiscovery?: boolean }
export interface ProfileEvents { readonly profileChanged: AccountProfile | undefined }
export interface ProfileManagerOptions extends ClientOptions {
    readonly store: MeshlineStore; readonly relayClients: RelayClientPool; readonly accountManager: AccountManager; readonly deviceSigner: DeviceSigner;
}
const method = 'profile.publish';
const profileKey = (account: string): RecordKey => ({ collection: 'profiles', key: account });
const clone = (value: AccountProfile): AccountProfile => accountProfileCodec.decode(accountProfileCodec.encode(value));
const fingerprint = (value: JsonObject | undefined): string => canonicalJson(value ?? null);
function copyUpdate(value: ProfileUpdate): ProfileUpdate {
    for (const key of Object.keys(value)) if (!['nickname', 'bio', 'avatar', 'publicDiscovery'].includes(key)) throw new TypeError(`Unknown profile update field ${key}.`);
    if (value.publicDiscovery !== undefined && typeof value.publicDiscovery !== 'boolean') throw new TypeError('publicDiscovery must be boolean and cannot be deleted.');
    for (const field of [value.nickname, value.bio]) if (field !== undefined && field !== null && typeof field !== 'string') throw new TypeError('Profile text must be a string or null.');
    return { ...value, ...(value.avatar == null ? {} : { avatar: contentReferenceCodec.decode(contentReferenceCodec.encode(value.avatar)) }) };
}
function apply(update: ProfileUpdate, basis: AccountProfile): AccountProfile {
    const document = accountProfileCodec.encode(basis);
    for (const field of ['nickname', 'bio', 'avatar'] as const) {
        const value = update[field];
        if (value === null) delete document[field];
        else if (value !== undefined) document[field] = typeof value === 'string' ? value : contentReferenceCodec.encode(value);
    }
    if (update.publicDiscovery !== undefined) document.public_discovery = update.publicDiscovery;
    return accountProfileCodec.decode(document);
}

export class ProfileManager extends ClientComponent {
    readonly #store: MeshlineStore; readonly #pool: RelayClientPool; readonly #account: AccountManager; readonly #signer: DeviceSigner;
    readonly #gate = new AsyncGate(); readonly #events = new EventHub<ProfileEvents>();
    #profile: AccountProfile | undefined;
    #readsStopping = false;
    readonly #reads = new Map<string, MetadataCache<ProfileResolveResult>>();
    readonly #saveGate = new AsyncGate();
    constructor(options: ProfileManagerOptions) {
        super(options);
        for (const dependency of [options.relayClients, options.accountManager])
            if (dependency.accountId !== this.accountId || dependency.context.toString() !== this.context.toString())
                throw new ProtocolError('invalid_context', 'Profile dependencies belong to another account or network.');
        this.#store = options.store; this.#pool = options.relayClients; this.#account = options.accountManager; this.#signer = options.deviceSigner;
    }
    get profile(): AccountProfile | undefined { return this.#profile && clone(this.#profile); }
    on<K extends keyof ProfileEvents>(event: K, listener: EventListener<ProfileEvents[K]>): () => void { return this.#events.on(event, listener); }
    protected override async onInitialize(signal: AbortSignal): Promise<void> {
        this.#account.ensureInitialized();
        await this.#store.initialize({ context: this.context.toString(), accountId: this.accountId }, signal);
        for (const row of (await this.#store.read([{ collection: 'profiles' }], signal)).sets[0]!) {
            const result = profileResolveResultCodec.decode(row.value); validateProfileResult(result, this.context, row.key);
            this.#reads.set(row.key, new MetadataCache(this.clock, result));
            if (row.key === this.accountId) this.#profile = result.profile;
        }
    }
    #accept(profile: AccountProfile | undefined): boolean {
        const changed = (profile && accountProfileCodec.stringify(profile)) !== (this.#profile && accountProfileCodec.stringify(this.#profile));
        this.#profile = profile && clone(profile); return changed;
    }
    #changed(): void { this.#events.notify('profileChanged', this.profile, error => this.notifyBackgroundError({ operation: 'observer', resource: 'profileChanged', error })); }
    async getProfile(accountId = this.accountId, signal?: AbortSignal): Promise<AccountProfile | undefined> { return this.#read(accountId, signal); }
    async refreshProfile(accountId = this.accountId, signal?: AbortSignal): Promise<AccountProfile | undefined> { return this.#read(accountId, signal, true); }
    async #read(accountId: string, signal?: AbortSignal, force = false): Promise<AccountProfile | undefined> {
        validateAccountId(accountId);
        return this.runOperation(async scope => {
            if (this.#readsStopping) throw new DOMException('Profile refreshes are stopping.', 'AbortError');
            let cache = this.#reads.get(accountId);
            if (!cache) { cache = new MetadataCache(this.clock); this.#reads.set(accountId, cache); }
            const selected = cache;
            const result = await cache.read(() => true, token => this.runOperation(async querySignal => {
                const previous = selected.value;
                const resolved = await this.#resolve(accountId, await this.#home(querySignal), querySignal);
                if (!resolved) return undefined;
                return this.#saveGate.run(async () => {
                    if (selected.value !== previous && selected.value && selected.value.profile.updatedAt >= resolved.profile.updatedAt) return selected.value;
                    await this.#save(resolved, querySignal); return resolved;
                }, querySignal);
            }, token), error => this.notifyBackgroundError({ operation: 'synchronize', resource: accountId, error }), scope, force);
            return result && clone(result.profile);
        }, signal);
    }
    async updateProfile(update: ProfileUpdate, signal?: AbortSignal): Promise<AccountProfile> {
        const copied = copyUpdate(update); let changed = false;
        const result = await this.runOperation(scope => this.#gate.run(async () => {
            const result = await this.#publish(copied, undefined, scope); changed = this.#accept(result); return result;
        }, scope), signal);
        if (changed) this.#changed(); return clone(result);
    }
    /** @internal Initializes a missing profile without applying defaults over retained or remote values. */
    initializeAccountProfile(initial: AccountInitialProfile | undefined, createIfMissing: boolean, signal: AbortSignal): Promise<void> {
        const copied = copyUpdate(initial ?? {});
        return this.runOperation(scope => this.#gate.run(async () => {
            if (!createIfMissing) { await this.refreshProfile(undefined, scope); return; }
            const published = await this.#publish(copied, undefined, scope, true);
            if (this.#accept(published)) this.#changed();
        }, scope), signal);
    }
    /** Republish the newest known local/pending/supplied snapshot, including during home-relay migration. */
    async publishProfile(profile?: AccountProfile, signal?: AbortSignal): Promise<AccountProfile | undefined> {
        const supplied = profile && clone(profile);
        if (supplied) { validateProfile(supplied); if (supplied.account !== this.accountId) throw new ProtocolError('invalid_identity', 'Profile belongs to another account.'); }
        let changed = false;
        const result = await this.runOperation(scope => this.#gate.run(async () => {
            const snapshot = await this.#store.read([profileKey(this.accountId), requestKey(method)], scope);
            const local = snapshot.sets[0]![0]?.value;
            const signed = readSignedRequest(snapshot.sets[1]![0]?.value);
            let latest = local && profileResolveResultCodec.decode(local).profile;
            if (signed?.pending && (!latest || signed.revision >= latest.updatedAt)) latest = accountProfileCodec.decode(signed.document);
            if (supplied && (!latest || supplied.updatedAt > latest.updatedAt)) latest = supplied;
            if (!latest) return undefined;
            const published = await this.#publish({}, latest, scope); changed = this.#accept(published); return published;
        }, scope), signal);
        if (changed) this.#changed(); return result && clone(result);
    }
    async #home(signal: AbortSignal): Promise<string> {
        const route = await this.#account.getRoute(undefined, signal);
        if (!route) throw new ProtocolError('route_required', 'Establish or recover the account route before publishing profiles.');
        return route.relayId;
    }
    async #resolve(account: string, relayId: string, signal: AbortSignal): Promise<ProfileResolveResult | undefined> {
        const client = await this.#pool.get(relayId, { mode: 'device', signer: this.#signer }, signal);
        let response;
        try { response = await client.requestHttp('GET', 'profile.resolve', { account }, { signal }); }
        catch (error) { if (error instanceof RelayError && error.code === 'not_found') return undefined; throw error; }
        if (response === undefined) throw new ProtocolError('invalid_response', 'Profile resolution returned no document.');
        const result = profileResolveResultCodec.decode(response); validateProfileResult(result, this.context, account); return result;
    }
    async #publish(update: ProfileUpdate, restored: AccountProfile | undefined, signal: AbortSignal, initializeOnly = false): Promise<AccountProfile> {
        const certificate = deviceCertificateCodec.decode(deviceCertificateCodec.encode(this.#signer.certificate));
        if (certificate.account !== this.accountId) throw new ProtocolError('invalid_identity', 'Device signer belongs to another account.');
        const relayId = await this.#home(signal);
        const queries = [profileKey(this.accountId), requestKey(method)];
        const snapshot = await this.#store.read(queries, signal);
        const local = snapshot.sets[0]![0]?.value; const saved = snapshot.sets[1]![0]?.value;
        let signed = readSignedRequest(saved); let request: AccountProfile;
        const initial = initializeOnly ? await this.#resolve(this.accountId, relayId, signal) : undefined;
        if (initial) {
            await this.#saveGate.run(() => this.#save(initial, signal), signal);
            return initial.profile;
        }
        if (initializeOnly && local) throw new ProtocolError('profile_unavailable', 'A retained profile is unavailable remotely; initial values cannot replace it.');
        if (initializeOnly && signed && !signed.pending && signed.relayId === relayId) { restored = accountProfileCodec.decode(signed.document); update = {}; }
        if (signed?.pending && (!restored || signed.relayId === relayId)) {
            request = accountProfileCodec.decode(signed.document);
            if (signed.relayId !== relayId || !initializeOnly && accountProfileCodec.stringify(apply(update, request)) !== accountProfileCodec.stringify(request))
                throw new StateConflictError('A profile publication has an unknown result. Retry the original update or resolve it first.');
        } else {
            const current = initializeOnly ? undefined : await this.#resolve(this.accountId, relayId, signal);
            const cached = local && profileResolveResultCodec.decode(local).profile;
            let basis = current?.profile;
            if (basis && cached && basis.updatedAt < cached.updatedAt) throw new ProtocolError('stale_profile', 'Relay returned an older profile.');
            for (const candidate of [cached, restored, signed?.pending ? accountProfileCodec.decode(signed.document) : undefined])
                if (candidate && (!basis || candidate.updatedAt > basis.updatedAt)) basis = candidate;
            basis ??= { account: this.accountId, publicDiscovery: false, updatedAt: 0, deviceSignature: new Uint8Array(64) };
            request = { ...apply(update, basis), updatedAt: Math.max(this.clock.nowSeconds(), basis.updatedAt), deviceSignature: new Uint8Array(64) };
            validateProfile(request);
            request = { ...request, deviceSignature: (await this.#signer.sign(profileInput(request, this.context), signal)).slice() };
            validateProfileResult({ profile: request, signerCertificate: certificate }, this.context, this.accountId);
            const prepared: SignedRequest = { relayId, revision: request.updatedAt, document: accountProfileCodec.encode(request), pending: true };
            await updateStore(this.#store, queries, state => {
                if (fingerprint(state.sets[0]![0]?.value) !== fingerprint(local) || fingerprint(state.sets[1]![0]?.value) !== fingerprint(saved))
                    throw new StateConflictError('Profile changed while signing. Resolve current state before retrying.');
                return { mutations: [putSignedRequest(method, prepared)], result: undefined };
            }, signal);
            signed = prepared;
        }
        // A renewed certificate with the same key is valid; a replaced signing identity cannot replay the old request.
        validateProfileResult({ profile: request, signerCertificate: certificate }, this.context, this.accountId);
        const currentCertificate = this.#signer.certificate;
        if (currentCertificate.account !== certificate.account || canonicalJson(deviceCertificateCodec.encode(currentCertificate)) !== canonicalJson(deviceCertificateCodec.encode(certificate)))
            throw new StateConflictError('Device signer changed during profile publication.');
        const client = await this.#pool.get(relayId, { mode: 'device', signer: this.#signer }, signal);
        try { await client.requestHttp('PUT', method, signed!.document, { signal }); }
        catch (error) {
            if (error instanceof RelayError && error.isDefinitiveRejection) {
                try { await finishSignedRequest(this.#store, method, signed!); }
                catch (storageError) { throw new AggregateError([error, storageError], 'Profile rejection could not be persisted.'); }
            }
            if (initializeOnly && !(error instanceof RelayError && error.isDefinitiveRejection) && !signal.aborted) throw new SubmissionOutcomeError('Profile publication outcome is unknown.', { cause: error });
            throw error;
        }
        await this.#saveGate.run(async () => {
            await this.#save({ profile: request, signerCertificate: certificate }, signal);
            this.#reads.get(this.accountId)!.recordSuccess(this.clock.monotonicMilliseconds());
        }, signal); return request;
    }
    async #save(result: ProfileResolveResult, signal: AbortSignal): Promise<void> {
        const profile = result.profile;
        await updateStore(this.#store, [profileKey(profile.account), requestKey(method)], snapshot => {
            const previous = snapshot.sets[0]![0]?.value;
            if (previous && profile.updatedAt < profileResolveResultCodec.decode(previous).profile.updatedAt)
                throw new ProtocolError('stale_profile', 'Returned profile is older than the known profile.');
            const mutations: StoreMutation[] = [{ ...profileKey(profile.account), kind: 'put', value: profileResolveResultCodec.encode(result) }];
            const signed = readSignedRequest(snapshot.sets[1]![0]?.value);
            if (profile.account === this.accountId && signed?.pending && (profile.updatedAt > signed.revision
                || profile.updatedAt === signed.revision && accountProfileCodec.stringify(profile) === canonicalJson(signed.document)))
                mutations.push(putSignedRequest(method, { ...signed, pending: false }));
            return { mutations, result: undefined };
        }, signal);
        let cache = this.#reads.get(profile.account);
        if (!cache) { cache = new MetadataCache(this.clock); this.#reads.set(profile.account, cache); }
        cache.publish(result);
        if (profile.account === this.accountId && this.#accept(profile)) this.#changed();
    }
    protected override async onStop(): Promise<void> { this.#readsStopping = true; try { await Promise.all([...this.#reads.values()].map(cache => cache.stop())); } finally { this.#readsStopping = false; } }
    protected override async onDispose(): Promise<void> { await Promise.all([...this.#reads.values()].map(cache => cache.stop(true))); this.#reads.clear(); this.#events.clear(); }
}
