import { ProtocolError } from '../errors.js';
import { validateAccountId } from '../identity/neo.js';
import type { DeviceSigner } from '../interactions.js';
import { contentReferenceCodec, type ContentReference } from '../models/content.js';
import { deviceCertificateCodec } from '../models/identity.js';
import { accountProfileCodec, profileInput, profileResolveResultCodec, validateProfile, validateProfileResult, type AccountProfile, type ProfileResolveResult } from '../models/profile.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { MetadataCache } from '../runtime/metadata-cache.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { EventHub, type EventListener } from '../runtime/events.js';
import { readSignedRequest, requestKey } from '../storage/signed-request.js';
import type { MeshlineStore, RecordKey, StoreMutation } from '../storage/store.js';
import { updateStore } from '../storage/transaction.js';
import type { RelayClientPool } from '../transport/pool.js';
import { RelayError } from '../transport/relay-error.js';
import type { AccountManager } from './account.js';
import { ClientComponent, type ClientOptions } from './component.js';
import type { AccountInitialProfile } from '../client/account-establishment.js';
import { SubmissionOutcomeError } from './authorization-error.js';
import { StateUpdateCoordinator, type StateUpdateAttempt, type StateUpdateFailure } from './state-update-coordinator.js';
import { canReuseRequest, isCurrentRequest, supersedeRequest } from '../storage/state-publication.js';

/** Omitted fields retain their values; null deletes optional fields. */
export interface ProfileUpdate { readonly nickname?: string | null; readonly bio?: string | null; readonly avatar?: ContentReference | null; readonly publicDiscovery?: boolean }
/** Publication status of the current account's latest local profile update. */
export type ProfileUpdateStatus = 'synchronized' | 'pending' | 'publishing' | 'waitingToRetry' | 'failed';
/** Diagnostics from the most recent failed attempt. */
export interface ProfileUpdateError {
    readonly message: string;
    /** Relay error code; absent for local or transport failures. */
    readonly code?: string;
    /** UTC Unix time in seconds. */
    readonly occurredAt: number;
}
/** Detached local state. Editing pendingUpdate does not submit changes. */
export interface ProfileUpdateState {
    readonly status: ProfileUpdateStatus;
    /** Merged field edits; absent when synchronized. Pure republication has an empty update. */
    readonly pendingUpdate?: ProfileUpdate;
    /** Retained during retries; cleared by new intent or confirmation. */
    readonly lastError?: ProfileUpdateError;
    /** Scheduled UTC Unix time in seconds; absent while publishing, synchronized or paused. Automatic attempts require a running component. */
    readonly nextAttemptAt?: number;
}
export interface ProfileEvents {
    readonly profileChanged: AccountProfile | undefined;
    /** Re-read getUpdateState after notification. Observer failures reach backgroundError without changing publication results. */
    readonly updateStateChanged: void;
}
export interface ProfileManagerOptions extends ClientOptions {
    readonly store: MeshlineStore; readonly relayClients: RelayClientPool; readonly accountManager: AccountManager; readonly deviceSigner: DeviceSigner;
}
const method = 'profile.publish';
const profileKey = (account: string): RecordKey => ({ collection: 'profiles', key: account });
const clone = (value: AccountProfile): AccountProfile => accountProfileCodec.decode(accountProfileCodec.encode(value));
const cloneCertificate = (value: DeviceSigner['certificate']): DeviceSigner['certificate'] => deviceCertificateCodec.decode(deviceCertificateCodec.encode(value));
function unwrapPublicationError(error: unknown): unknown {
    if (error instanceof SubmissionOutcomeError) return error.cause;
    if (error instanceof AggregateError && error.errors.some(hasSubmissionError))
        return new AggregateError(error.errors.map(unwrapPublicationError), error.message);
    return error;
}
function hasSubmissionError(error: unknown): boolean {
    return error instanceof SubmissionOutcomeError || error instanceof AggregateError && error.errors.some(hasSubmissionError);
}
function readUpdateState(row: JsonObject | undefined, publishing: boolean, next: number | null): ProfileUpdateState {
    if (!row?.pending) return { status: 'synchronized' };
    const intent = row.intent == null ? {} : requireObject(row.intent);
    const pendingUpdate: ProfileUpdate = {
        ...(intent.nickname === undefined ? {} : { nickname: intent.nickname as string | null }),
        ...(intent.bio === undefined ? {} : { bio: intent.bio as string | null }),
        ...(intent.avatar === undefined ? {} : { avatar: intent.avatar === null ? null : contentReferenceCodec.decode(intent.avatar) }),
        ...(intent.public_discovery === undefined ? {} : { publicDiscovery: intent.public_discovery as boolean }),
    };
    let lastError: ProfileUpdateError | undefined;
    if (row.lastError != null) {
        const error = requireObject(row.lastError);
        if (typeof error.message !== 'string' || error.code !== undefined && typeof error.code !== 'string')
            throw new ProtocolError('invalid_storage', 'Invalid profile update diagnostics.');
        requireSafeInteger(error.occurredAt, 0);
        lastError = { message: error.message, occurredAt: error.occurredAt, ...(error.code === undefined ? {} : { code: error.code as string }) };
    }
    return { status: publishing ? 'publishing' : next === null ? 'failed' : lastError ? 'waitingToRetry' : 'pending', pendingUpdate,
        ...(lastError ? { lastError } : {}), ...(!publishing && next !== null ? { nextAttemptAt: next } : {}) };
}
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
    readonly #events = new EventHub<ProfileEvents>();
    #profile: AccountProfile | undefined;
    #readsStopping = false;
    readonly #reads = new Map<string, MetadataCache<ProfileResolveResult>>();
    readonly #saveGate = new AsyncGate();
    readonly #updates: StateUpdateCoordinator<AccountProfile>;
    constructor(options: ProfileManagerOptions) {
        super(options);
        for (const dependency of [options.relayClients, options.accountManager])
            if (dependency.accountId !== this.accountId || dependency.context.toString() !== this.context.toString())
                throw new ProtocolError('invalid_context', 'Profile dependencies belong to another account or network.');
        this.#store = options.store; this.#pool = options.relayClients; this.#account = options.accountManager; this.#signer = options.deviceSigner;
        const owner = this;
        this.#updates = new class extends StateUpdateCoordinator<AccountProfile> {
            protected readonly operation = method;
            protected readonly query = requestKey(method);
            protected key(): RecordKey { return requestKey(method); }
            protected resource(): string { return owner.accountId; }
            protected create(): JsonObject { return { relayId: '', revision: -1 }; }
            protected override beforeMerge(previous: JsonObject, patch: JsonObject): void {
                // Snapshots are candidate bases; explicit fields remain separate user intent.
                if (previous.snapshot != null && patch.snapshot != null
                    && accountProfileCodec.decode(previous.snapshot).updatedAt >= accountProfileCodec.decode(patch.snapshot).updatedAt) delete patch.snapshot;
                if (patch.initialize_only !== true && ['nickname', 'bio', 'avatar', 'public_discovery'].some(field => field in patch)) delete previous.initialize_only;
            }
            protected publish(attempt: StateUpdateAttempt<AccountProfile>, signal: AbortSignal): Promise<AccountProfile> { return owner.#publishLatest(attempt, signal); }
            protected report(resource: string, error: unknown): void { owner.notifyBackgroundError({ operation: 'synchronize', resource, error: unwrapPublicationError(error) }); }
            protected override changed(): void { owner.#updateStateChanged(); }
            protected override setError(row: JsonObject, failure: StateUpdateFailure | undefined): void {
                const error = unwrapPublicationError(failure?.error);
                row.lastError = failure ? { message: error instanceof Error ? error.message : String(error), occurredAt: failure.occurredAt,
                    ...(error instanceof RelayError ? { code: error.code } : {}) } : null;
            }
            protected override retryAt(row: JsonObject, error: unknown, failures: number): number | null {
                error = unwrapPublicationError(error);
                const next = super.retryAt(row, error, failures);
                return next !== null && error instanceof RelayError && error.code === 'clock_skew' ? Math.max(next, Number(row.revision)) : next;
            }
        }(this.#store, this.clock);
    }
    /** Confirmed cached profile. Pending edits are exposed by getUpdateState. */
    get profile(): AccountProfile | undefined { return this.#profile && clone(this.#profile); }
    on<K extends keyof ProfileEvents>(event: K, listener: EventListener<ProfileEvents[K]>): () => void { return this.#events.on(event, listener); }
    /** Reads local pending fields and publication state after initialize(), including before start(). No network access. */
    getUpdateState(signal?: AbortSignal): Promise<ProfileUpdateState> {
        return this.runOperation(scope => this.#updates.read(this.accountId, readUpdateState, scope), signal);
    }
    #updateStateChanged(): void {
        this.#events.notify('updateStateChanged', undefined, error => this.notifyBackgroundError({ operation: 'observer', resource: 'updateStateChanged', error }));
    }
    protected override async onInitialize(signal: AbortSignal): Promise<void> {
        this.#account.ensureInitialized();
        await this.#store.initialize({ context: this.context.toString(), accountId: this.accountId }, signal);
        for (const row of (await this.#store.read([{ collection: 'profiles' }], signal)).sets[0]!) {
            const result = profileResolveResultCodec.decode(row.value); validateProfileResult(result, this.context, row.key);
            this.#reads.set(row.key, new MetadataCache(this.clock, result));
            if (row.key === this.accountId) this.#profile = result.profile;
        }
        await this.#importState(signal);
    }
    async #importState(signal: AbortSignal): Promise<void> {
        const row = (await this.#store.read([requestKey(method)], signal)).sets[0]![0]?.value;
        if (!row?.pending || row.intent != null || row.document == null) return;
        const profile = accountProfileCodec.encode(accountProfileCodec.decode(row.document));
        const intent: JsonObject = {};
        const retained = await this.#store.read([{ collection: 'client_account_operations', key: 'establishment' }, profileKey(this.accountId)], signal);
        const setup = retained.sets[0]![0]?.value;
        // Missing intent alone cannot distinguish legacy user edits from initial values.
        if (setup?.initialAccount === true && setup.step === 'initializingProfile' && setup.relayId === row.relayId && !retained.sets[1]!.length) {
            const initial = setup.initialProfile == null ? {} : requireObject(setup.initialProfile);
            if (profile.account === this.accountId && profile.public_discovery === (initial.publicDiscovery ?? false)
                && ['nickname', 'bio', 'avatar'].every(field => canonicalJson(profile[field] ?? null) === canonicalJson(initial[field] ?? null))) intent.initialize_only = true;
        }
        for (const field of ['nickname', 'bio', 'avatar', 'public_discovery']) intent[field] = profile[field] ?? null;
        await this.#updates.import(this.accountId, intent, signal);
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
                return this.#saveDiscovered(resolved, previous, querySignal);
            }, token), error => this.notifyBackgroundError({ operation: 'synchronize', resource: accountId, error }), scope, force);
            return result && clone(result.profile);
        }, signal);
    }
    /** Merges profile fields with unfinished changes; later values win. */
    async updateProfile(update: ProfileUpdate, signal?: AbortSignal): Promise<AccountProfile> {
        const copied = copyUpdate(update);
        validateProfile(apply(copied, { account: this.accountId, publicDiscovery: false, updatedAt: 0, deviceSignature: new Uint8Array(64) }));
        const patch: JsonObject = {};
        for (const field of ['nickname', 'bio', 'avatar'] as const) if (copied[field] !== undefined)
            patch[field] = field === 'avatar' && copied.avatar !== null ? contentReferenceCodec.encode(copied.avatar!) : copied[field] as string | null;
        if (copied.publicDiscovery !== undefined) patch.public_discovery = copied.publicDiscovery;
        const result = await this.runOperation(scope => this.#updates.submit(this.accountId, patch, scope), signal)
            .catch(error => { throw unwrapPublicationError(error); });
        return clone(result);
    }
    /** @internal Initializes a missing profile without applying defaults over retained or remote values. */
    initializeAccountProfile(initial: AccountInitialProfile | undefined, createIfMissing: boolean, signal: AbortSignal): Promise<void> {
        const copied = copyUpdate(initial ?? {});
        return this.runOperation(async scope => {
            const previous = this.#reads.get(this.accountId)?.value;
            const existing = await this.#resolve(this.accountId, await this.#home(scope), scope);
            if (existing) { await this.#saveDiscovered(existing, previous, scope); return; }
            if (!createIfMissing) return;
            if ((await this.#store.read([profileKey(this.accountId)], scope)).sets[0]!.length)
                throw new ProtocolError('profile_unavailable', 'A retained profile is unavailable remotely; initial values cannot replace it.');
            const intent: JsonObject = { initialize_only: true, public_discovery: copied.publicDiscovery ?? false };
            for (const field of ['nickname', 'bio', 'avatar'] as const) if (copied[field] != null)
                intent[field] = field === 'avatar' ? contentReferenceCodec.encode(copied.avatar!) : copied[field] as string;
            try {
                await this.#updates.resume(this.accountId, intent, scope);
            } catch (error) {
                if (error instanceof AggregateError && hasSubmissionError(error))
                    throw new SubmissionOutcomeError('Profile publication outcome is unknown.', { cause: error });
                throw error;
            }
        }, signal);
    }
    /** Republishes the newest available profile with pending edits. Supplied snapshots use updatedAt; ties retain the pending snapshot. */
    async publishProfile(profile?: AccountProfile, signal?: AbortSignal): Promise<AccountProfile | undefined> {
        const supplied = profile && clone(profile);
        if (supplied) { validateProfile(supplied); if (supplied.account !== this.accountId) throw new ProtocolError('invalid_identity', 'Profile belongs to another account.'); }
        const result = await this.runOperation(async scope => {
            if (!supplied) {
                const snapshot = await this.#store.read([profileKey(this.accountId), requestKey(method)], scope);
                if (!snapshot.sets[0]!.length && !snapshot.sets[1]![0]?.value.pending) return undefined;
            }
            // Read cached/prepared documents in the owning attempt, after admission.
            return this.#updates.submit(this.accountId, supplied ? { snapshot: accountProfileCodec.encode(supplied) } : {}, scope);
        }, signal).catch(error => { throw unwrapPublicationError(error); });
        return result && clone(result);
    }
    async #publishLatest(attempt: StateUpdateAttempt<AccountProfile>, signal: AbortSignal): Promise<AccountProfile> {
        const relayId = await this.#home(signal); const certificate = cloneCertificate(this.#signer.certificate);
        const snapshot = await this.#store.read([profileKey(this.accountId), requestKey(method)], signal);
        const saved = readSignedRequest(snapshot.sets[1]![0]?.value)!;
        const reusable = saved.pending && canReuseRequest(saved.requestState!) && canonicalJson(saved.document ?? null) !== attempt.rejectedRequest ? saved.document : undefined;
        const resolved = await this.#resolve(this.accountId, relayId, signal);
        if (attempt.intent.initialize_only === true) {
            if (resolved) { await this.#confirm(attempt, resolved, signal); return resolved.profile; }
            if (snapshot.sets[0]!.length) throw new ProtocolError('profile_unavailable', 'A retained profile is unavailable remotely; initial values cannot replace it.');
        }
        let basis = resolved?.profile;
        for (const candidate of [attempt.intent.snapshot, snapshot.sets[0]![0]?.value.profile, reusable, attempt.request]) {
            if (candidate == null) continue;
            const profile = accountProfileCodec.decode(candidate);
            if (!basis || profile.updatedAt >= basis.updatedAt) basis = profile;
        }
        basis ??= { account: this.accountId, publicDiscovery: false, updatedAt: 0, deviceSignature: new Uint8Array(64) };
        const document = accountProfileCodec.encode(basis);
        for (const field of ['nickname', 'bio', 'avatar', 'public_discovery']) {
            if (!(field in attempt.intent)) continue;
            if (attempt.intent[field] === null) delete document[field]; else document[field] = attempt.intent[field]!;
        }
        let request = accountProfileCodec.decode(document);
        const retained = attempt.request ?? (saved.relayId === relayId ? reusable : undefined);
        if (!retained || accountProfileCodec.stringify(request) !== canonicalJson(retained)) {
            const updatedAt = Math.max(this.clock.nowSeconds(), Math.max(basis.updatedAt, saved.revision) + 1);
            request = { ...request, updatedAt, deviceSignature: new Uint8Array(64) }; validateProfile(request);
            request = { ...request, deviceSignature: (await this.#signer.sign(profileInput(request, this.context), signal)).slice() };
        }
        validateProfileResult({ profile: request, signerCertificate: certificate }, this.context, this.accountId);
        const wire = accountProfileCodec.encode(request);
        await attempt.saveRequest(wire, { relayId, revision: request.updatedAt }, signal);
        const client = await this.#pool.get(relayId, { mode: 'device', signer: this.#signer }, signal);
        await attempt.check(signal);
        try { await client.requestHttp('PUT', method, wire, { signal }); }
        catch (error) {
            if (error instanceof RelayError && error.isDefinitiveRejection) {
                if (error.code !== 'clock_skew') attempt.recordRejection(wire);
                throw error;
            }
            throw new SubmissionOutcomeError('Profile publication outcome is unknown.', { cause: error });
        }
        await this.#confirm(attempt, { profile: request, signerCertificate: certificate }, signal);
        return request;
    }
    async #confirm(attempt: StateUpdateAttempt<AccountProfile>, result: ProfileResolveResult, signal: AbortSignal): Promise<void> {
        let changed = false;
        await attempt.current(() => this.#saveGate.run(async () => {
            changed = (await this.#save(result, signal, true)).changed;
            this.#reads.get(this.accountId)!.recordSuccess(this.clock.monotonicMilliseconds());
        }, signal), signal);
        if (changed) attempt.deferNotification(() => this.#changed());
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
    async #saveDiscovered(result: ProfileResolveResult, previous: ProfileResolveResult | undefined, signal: AbortSignal): Promise<ProfileResolveResult> {
        // Always acquire publication before cache; notify after releasing both.
        const save = () => this.#saveGate.run(async () => {
            const current = this.#reads.get(result.profile.account)?.value;
            if (current !== previous && current && current.profile.updatedAt >= result.profile.updatedAt)
                return { result: current, changed: false, confirmed: false };
            return { result, ...await this.#save(result, signal) };
        }, signal);
        const saved = result.profile.account === this.accountId
            ? await this.#updates.withPublicationLock(this.accountId, save, signal) : await save();
        if (saved.confirmed) this.#updateStateChanged();
        if (saved.changed) this.#changed();
        return saved.result;
    }
    async #save(result: ProfileResolveResult, signal: AbortSignal, published = false): Promise<{ changed: boolean; confirmed: boolean }> {
        const profile = result.profile;
        const confirmed = await updateStore(this.#store, [profileKey(profile.account), requestKey(method)], snapshot => {
            const previous = snapshot.sets[0]![0]?.value;
            if (previous && profile.updatedAt < profileResolveResultCodec.decode(previous).profile.updatedAt)
                throw new ProtocolError('stale_profile', 'Returned profile is older than the known profile.');
            const mutations: StoreMutation[] = [{ ...profileKey(profile.account), kind: 'put', value: profileResolveResultCodec.encode(result) }];
            const raw = snapshot.sets[1]![0]?.value;
            const signed = readSignedRequest(raw);
            const initial = signed?.pending && signed.intent?.initialize_only === true;
            const confirmed = profile.account === this.accountId && signed?.pending === true && (initial || (!signed.intent || isCurrentRequest(signed.requestState!))
                && (profile.updatedAt > signed.revision || profile.updatedAt === signed.revision && accountProfileCodec.stringify(profile) === canonicalJson(signed.document ?? null)));
            if (confirmed) {
                const generation = (signed.generation ?? 0) + (initial && !published ? 1 : 0);
                requireSafeInteger(generation, 0);
                mutations.push({ ...requestKey(method), kind: 'put', value: { ...raw, generation,
                    requestState: initial && !published ? supersedeRequest(signed.requestState!) : signed.requestState!,
                    pending: false, intent: null, nextAttemptAt: null, lastError: null } });
            }
            return { mutations, result: confirmed };
        }, signal);
        let cache = this.#reads.get(profile.account);
        if (!cache) { cache = new MetadataCache(this.clock); this.#reads.set(profile.account, cache); }
        cache.publish(result);
        return { changed: profile.account === this.accountId && this.#accept(profile), confirmed };
    }
    protected override async onStart(signal: AbortSignal): Promise<void> { await this.#importState(signal); this.#updates.start(this.runtimeSignal); }
    protected override async onStop(): Promise<void> { this.#readsStopping = true; try { await Promise.all([this.#updates.stop(), ...[...this.#reads.values()].map(cache => cache.stop())]); } finally { this.#readsStopping = false; } }
    protected override async onDispose(): Promise<void> { await this.#updates.drain(); await Promise.all([...this.#reads.values()].map(cache => cache.stop(true))); this.#reads.clear(); this.#events.clear(); }
}
