import { GroupKeyAccessError } from '../crypto/groups.js';
import { MessageKeyAccessError } from '../crypto/messages.js';
import { ProtocolError, StateConflictError } from '../errors.js';
import type { ResourceSyncBlockReason, ResourceSyncState, ResourceSyncStatus } from '../models/resource-sync.js';
import { RelayError } from '../transport/relay-error.js';
import { throwIfAborted, type RuntimeClock } from './clock.js';

export interface SyncBlock { readonly reason: ResourceSyncBlockReason; readonly error?: unknown }
export function classifySyncError(error: unknown): ResourceSyncBlockReason {
    if (error instanceof GroupKeyAccessError || error instanceof MessageKeyAccessError) return 'missingKey';
    if (error instanceof StateConflictError) return 'storage';
    if (error instanceof RelayError || error instanceof ProtocolError) {
        switch (error.code) {
            case 'unauthorized': case 'device_unknown': case 'authentication_required': case 'expired_session': case 'device_session_required': return 'authentication';
            case 'forbidden': return 'permission';
            case 'history_unavailable': return 'historyUnavailable';
            case 'bad_gateway': case 'temporarily_unavailable': case 'rate_limited': case 'socket_unavailable': case 'http_error': return 'connection';
            case 'invalid_storage': return 'storage';
        }
        if (error instanceof ProtocolError && /^(invalid_|missing_|conflicting_|unsupported_|unauthorized_epoch)/.test(error.code) || error.code === 'invalid_signature') return 'verification';
    }
    if (error instanceof Error && (error.name === 'TimeoutError' || error.name === 'AbortError')) return 'connection';
    return 'unknown';
}

/** In-memory snapshots; a stopped or superseded pass cannot overwrite a newer observation. */
export class ResourceSyncTracker {
    readonly #resources = new Map<string, { generation: number; status: ResourceSyncStatus; preserveOnStop: boolean }>();
    #generation = 0;
    constructor(readonly clock: RuntimeClock, readonly changed: (status: ResourceSyncStatus) => void) {}
    get(resource: string): ResourceSyncStatus { return this.#resources.get(resource)?.status ?? Object.freeze({ resource, state: 'idle', hasRetentionGap: false }); }
    observeGap(resource: string, notify = true): void {
        const current = this.get(resource); if (current.hasRetentionGap) return;
        const status = Object.freeze({ ...current, hasRetentionGap: true });
        const entry = this.#resources.get(resource);
        this.#resources.set(resource, { generation: entry?.generation ?? 0, status, preserveOnStop: entry?.preserveOnStop ?? false });
        if (notify) this.changed(status);
    }
    async run(resource: string, action: () => Promise<SyncBlock | undefined>, signal: AbortSignal, preserveOnStop = false): Promise<ResourceSyncStatus> {
        throwIfAborted(signal); const generation = ++this.#generation;
        this.#set(resource, generation, 'synchronizing', undefined, preserveOnStop);
        try {
            const block = await action(); throwIfAborted(signal);
            const status = this.#snapshot(resource, block ? 'blocked' : 'caughtUp', block);
            if (this.#resources.get(resource)?.generation === generation) {
                this.#resources.set(resource, { generation, status, preserveOnStop }); this.changed(status);
            }
            return status;
        } catch (error) {
            if (this.#resources.get(resource)?.generation === generation) this.#set(resource, generation, signal.aborted ? 'idle' : 'blocked', signal.aborted ? undefined : { reason: classifySyncError(error), error }, preserveOnStop);
            throw error;
        }
    }
    block(resource: string, error: unknown): void { this.#set(resource, ++this.#generation, 'blocked', { reason: classifySyncError(error), error }); }
    stop(resetAll = false): void {
        for (const [resource, entry] of this.#resources) {
            // Explicit synchronization has its own lifetime, independent of background work.
            if (!resetAll && entry.preserveOnStop) continue;
            if (entry.status.state !== 'idle') this.#set(resource, ++this.#generation, 'idle');
            else this.#resources.set(resource, { ...entry, generation: ++this.#generation, preserveOnStop: false });
        }
    }
    #set(resource: string, generation: number, state: ResourceSyncState, block?: SyncBlock, preserveOnStop = false): void {
        const status = this.#snapshot(resource, state, block);
        this.#resources.set(resource, { generation, status, preserveOnStop }); this.changed(status);
    }
    #snapshot(resource: string, state: ResourceSyncState, block?: SyncBlock): ResourceSyncStatus {
        const current = this.get(resource); const last = state === 'caughtUp' ? this.clock.nowSeconds() : current.lastSynchronizedAt;
        return Object.freeze({ resource, state, hasRetentionGap: current.hasRetentionGap,
            ...(last === undefined ? {} : { lastSynchronizedAt: last }), ...(block ? { blockReason: block.reason, ...(block.error === undefined ? {} : { error: block.error }) } : {}) });
    }
}
